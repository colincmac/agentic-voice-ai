using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Definition;
using Agents.AI.ContactCenter.IvrWorkflow.Guards;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;
using Agents.AI.ContactCenter.IvrWorkflow.Registry;
using Agents.AI.ContactCenter.IvrWorkflow.Strategies;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>
/// Default implementation of <see cref="IIvrWorkflowCompiler"/>. Lowers an
/// <see cref="IvrWorkflowDocument"/> into a runtime-ready <see cref="CompiledIvrWorkflow"/>,
/// preserving compatibility with the legacy <c>RealtimeIvrWorkflowDefinition</c> consumed
/// by existing strategy factories under <c>Calling/Strategies/*</c>.
/// </summary>
public sealed class IvrWorkflowCompiler : IIvrWorkflowCompiler
{
    private readonly IIvrToolRegistry _tools;
    private readonly IIvrPredicateRegistry _predicates;
    private readonly IReadOnlyDictionary<string, IIvrGuardFactory> _guardFactories;
    private readonly Func<IIvrWorkflowCatalog>? _catalogAccessor;

    // AsyncLocal cycle guard: tracks workflow names currently being compiled on this
    // logical async flow so a stage import that pulls the workflow being compiled in
    // (or any ancestor) throws a clear error instead of recursing forever. Uses
    // AsyncLocal rather than [ThreadStatic] because the catalog drives compilation
    // synchronously via .GetAwaiter().GetResult() over an async loader, which hops
    // thread-pool threads — [ThreadStatic] state doesn't flow across that boundary
    // and would cause cyclic imports to spin indefinitely instead of throwing.
    private static readonly AsyncLocal<HashSet<string>?> compileStack = new();

    public IvrWorkflowCompiler(
        IIvrToolRegistry tools,
        IIvrPredicateRegistry predicates,
        IEnumerable<IIvrGuardFactory>? guardFactories = null,
        Func<IIvrWorkflowCatalog>? catalogAccessor = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(predicates);
        _tools = tools;
        _predicates = predicates;
        _catalogAccessor = catalogAccessor;
        var factories = (guardFactories ?? BuiltInGuardFactories.CreateAll())
            .GroupBy(f => f.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        _guardFactories = factories;
    }

    public CompiledIvrWorkflow Compile(IvrWorkflowDocument document, IvrWorkflowSourceEntry? source = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Cycle guard: refuse to compile a workflow that is already being compiled in
        // this async flow (typical when stage imports load each other).
        var stack = compileStack.Value;
        if (stack is null)
        {
            stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            compileStack.Value = stack;
        }
        if (!stack.Add(document.Name))
        {
            throw new IvrWorkflowCompilationException(document.Name, [
                $"Import cycle detected: workflow '{document.Name}' is being compiled recursively. Check stage import chains."
            ]);
        }

        try
        {
            return CompileCore(document, source);
        }
        finally
        {
            stack.Remove(document.Name);
            if (stack.Count == 0)
            {
                compileStack.Value = null;
            }
        }
    }

    private CompiledIvrWorkflow CompileCore(IvrWorkflowDocument document, IvrWorkflowSourceEntry? source)
    {
        var errors = new List<string>();

        IvrStrategyPolicy workflowPolicy;
        try
        {
            workflowPolicy = MapStrategy(document.Strategy) ?? IvrStrategyPolicy.Default;
        }
        catch (InvalidOperationException ex)
        {
            errors.Add(ex.Message);
            workflowPolicy = IvrStrategyPolicy.Default;
        }

        var basePrompt = IvrPromptMapper.MapBasePrompt(document.Base?.Prompt);
        var baseToolNames = document.Base?.CommonTools ?? [];
        var baseRequiredAuth = ParseAuthLevel(document.Base?.RequiredAuthLevel);

        var capabilities = CompileCapabilities(document, errors);

        var intentExamples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var compiledStages = new List<CompiledIvrStage>(document.Stages.Count);
        var runtimeSteps = new List<RealtimeIvrWorkflowStep>(document.Stages.Count);

        foreach (var stageDoc in document.Stages)
        {
            var compiled = CompileStage(
                document,
                stageDoc,
                basePrompt,
                baseToolNames,
                baseRequiredAuth,
                capabilities,
                workflowPolicy,
                intentExamples,
                errors);

            compiledStages.Add(compiled);
            runtimeSteps.Add(compiled.RuntimeStep);
        }

        if (errors.Count > 0)
        {
            throw new IvrWorkflowCompilationException(document.Name, errors);
        }

        var authResolvers = CompileAuthResolvers(document, errors);
        if (errors.Count > 0)
        {
            throw new IvrWorkflowCompilationException(document.Name, errors);
        }

        var runtime = new RealtimeIvrWorkflowDefinition
        {
            Name = document.Name,
            Tier = workflowPolicy.Primary.ToTier(),
            BasePrompt = basePrompt,
            Steps = runtimeSteps,
            AuthResolvers = authResolvers,
            UnauthorizedFailureStepId = string.IsNullOrWhiteSpace(document.OnUnauthorized) ? null : document.OnUnauthorized,
        };

        var intents = intentExamples.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value,
            StringComparer.OrdinalIgnoreCase);

        return new CompiledIvrWorkflow
        {
            Name = document.Name,
            Description = document.Description,
            Version = document.Version,
            Runtime = runtime,
            Strategy = workflowPolicy,
            Stages = compiledStages,
            Capabilities = capabilities,
            IntentExamples = intents,
            Source = source,
        };
    }

    private CompiledIvrStage CompileStage(
        IvrWorkflowDocument document,
        IvrStageDocument stage,
        RealtimePrompt basePrompt,
        IReadOnlyList<string> baseToolNames,
        CallerVerificationLevel baseRequiredAuth,
        IReadOnlyDictionary<string, CompiledIvrCapability> capabilities,
        IvrStrategyPolicy workflowPolicy,
        Dictionary<string, List<string>> intentExamples,
        List<string> errors)
    {
        // Phase 2: stage import — pull a leaf stage from another workflow at compile time
        // and inline it under the local alias. No frame, no return contract; behaves
        // identically to a stage authored inline.
        if (stage.Import is { } importDoc)
        {
            return CompileImportStage(stage, importDoc, workflowPolicy, errors);
        }

        // Phase 1: subflow stages compile to a marker step the navigator pushes onto
        // its frame stack at entry time. They carry no prompt / tools / DTMF config —
        // those come from the child workflow's stages.
        if (string.Equals(stage.Type, "subflow", StringComparison.OrdinalIgnoreCase))
        {
            return CompileSubflowStage(stage, workflowPolicy, errors);
        }

        // Strategy resolution: stage strategy overrides workflow strategy.
        IvrStrategyPolicy stagePolicy;
        try
        {
            stagePolicy = MapStrategy(stage.Strategy) ?? workflowPolicy;
        }
        catch (InvalidOperationException ex)
        {
            errors.Add($"Stage '{stage.Id}': {ex.Message}");
            stagePolicy = workflowPolicy;
        }

        // Guards: stage-level requires + base required auth.
        var guards = new List<IIvrStepGuard>();
        if (baseRequiredAuth > CallerVerificationLevel.None)
        {
            guards.Add(new RequiredAuthLevelGuard(baseRequiredAuth));
        }
        foreach (var stageGuard in stage.Requires)
        {
            try
            {
                guards.Add(BuildGuard(document.Name, stage.Id, stageGuard));
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(ex.Message);
            }
        }
        // Inherit guards from referenced capabilities.
        var capabilityIds = new List<string>(stage.Capabilities.Count);
        foreach (var capId in stage.Capabilities)
        {
            if (!capabilities.TryGetValue(capId, out var cap))
            {
                errors.Add($"Stage '{stage.Id}': capability '{capId}' not found.");
                continue;
            }
            capabilityIds.Add(capId);
            foreach (var capGuard in cap.Guards)
            {
                guards.Add(capGuard);
            }
        }

        // Tools: base common tools + stage-declared + capability-supplied.
        var toolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in baseToolNames) { toolNames.Add(t); }
        if (stage.Realtime is { } realtime)
        {
            foreach (var t in realtime.Tools) { toolNames.Add(t); }
        }
        foreach (var capId in capabilityIds)
        {
            foreach (var tool in capabilities[capId].Tools)
            {
                // capability tools are already resolved AITool instances.
            }
        }

        var resolvedTools = ResolveTools(toolNames, errors, $"stage '{stage.Id}'");
        // Append capability tools that were already resolved at capability-compile time.
        foreach (var capId in capabilityIds)
        {
            foreach (var tool in capabilities[capId].Tools)
            {
                if (!resolvedTools.Any(t => string.Equals(t.Name, tool.Name, StringComparison.Ordinal)))
                {
                    resolvedTools.Add(tool);
                }
            }
        }

        // Tool rules: capability rules then stage rules (stage overrides take precedence by name).
        var ruleByName = new Dictionary<string, ToolUsageRule>(StringComparer.Ordinal);
        foreach (var capId in capabilityIds)
        {
            // capability tool rules are intentionally not stored on CompiledIvrCapability today;
            // they are merged from the YAML document directly.
            var capDoc = document.Capabilities.First(c => string.Equals(c.Id, capId, StringComparison.Ordinal));
            foreach (var rule in IvrPromptMapper.MapToolRules(capDoc.ToolRules) ?? [])
            {
                ruleByName[rule.Name] = rule;
            }
        }
        if (stage.Realtime is { } realtime2)
        {
            foreach (var rule in IvrPromptMapper.MapToolRules(realtime2.ToolRules) ?? [])
            {
                ruleByName[rule.Name] = rule;
            }
        }

        // Intents: index examples by name for the keyword classifier and collect compiled intents.
        var compiledIntents = new Dictionary<string, CompiledIvrIntent>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in stage.Intents)
        {
            if (string.IsNullOrWhiteSpace(i.Name))
            {
                continue;
            }
            compiledIntents[i.Name] = new CompiledIvrIntent
            {
                Name = i.Name,
                Examples = i.Examples,
                NextStageId = i.NextStage,
                CapabilityId = i.Capability,
                ConfirmPrompt = i.ConfirmPrompt,
            };

            if (!intentExamples.TryGetValue(i.Name, out var bag))
            {
                bag = [];
                intentExamples[i.Name] = bag;
            }
            foreach (var ex in i.Examples)
            {
                if (!bag.Contains(ex, StringComparer.OrdinalIgnoreCase))
                {
                    bag.Add(ex);
                }
            }
        }

        // Scripted (DTMF + NLU) tier configuration. Pass a guardBuilder so per-option
        // `requires:` lower to compiled IIvrStepGuard instances on each DtmfMenuOption.
        StepScriptedConfiguration? scriptedConfig = null;
        if (stage.Scripted is { } scriptedDoc)
        {
            IIvrStepGuard? PerOptionGuardBuilder(IvrGuardDocument g)
            {
                try
                {
                    return BuildGuard(document.Name, stage.Id, g);
                }
                catch (InvalidOperationException ex)
                {
                    errors.Add(ex.Message);
                    return null;
                }
            }
            scriptedConfig = IvrScriptedMapper.Map(scriptedDoc, stage.Id, errors, PerOptionGuardBuilder);
        }

        // ConversationState (instructions/examples/exit/transitions).
        var instructions = stage.Realtime?.Instructions.Count > 0
            ? stage.Realtime.Instructions
            : (stage.Goal is { Length: > 0 } goalText ? new[] { goalText } : (IReadOnlyList<string>)[]);

        var transitions = BuildTransitions(stage);
        var transitionRules = BuildTransitionRules(document.Name, stage, errors);

        var conversationState = new ConversationState
        {
            Id = stage.Id,
            Description = stage.Description ?? stage.Goal ?? stage.Id,
            Goal = stage.Goal,
            Instructions = instructions,
            Examples = stage.Realtime?.Examples?.Count > 0 ? stage.Realtime.Examples : null,
            ExitWhen = stage.ExitWhen,
            Transitions = transitions,
        };

        var runtimeStep = new RealtimeIvrWorkflowStep
        {
            Id = stage.Id,
            ConversationState = conversationState,
            AvailableTools = resolvedTools.Count == 0 ? null : resolvedTools,
            ToolRules = ruleByName.Count == 0 ? null : ruleByName.Values.ToList(),
            Guards = guards,
            RequiredStateKeys = stage.RequiredState,
            MaxRetries = stage.MaxRetries ?? 3,
            MaxDuration = ParseDuration(stage.MaxDuration, stage.Id, errors),
            RequiredAuthLevel = ResolveStageAuthLevel(baseRequiredAuth, stage.Requires),
            StepScriptedConfiguration = scriptedConfig,
            Terminal = stage.Terminal,
            TerminalOutcome = ParseTerminalOutcome(stage.TerminalOutcome),
            TransitionRules = transitionRules,
            OnUnauthorizedStepId = string.IsNullOrWhiteSpace(stage.OnUnauthorized) ? null : stage.OnUnauthorized,
            Intents = compiledIntents.Count == 0
                ? new Dictionary<string, RealtimeIvrWorkflowIntent>(StringComparer.OrdinalIgnoreCase)
                : compiledIntents.ToDictionary(
                    kv => kv.Key,
                    kv => new RealtimeIvrWorkflowIntent(
                        kv.Value.Name,
                        kv.Value.Examples,
                        kv.Value.NextStageId,
                        kv.Value.CapabilityId,
                        kv.Value.ConfirmPrompt),
                    StringComparer.OrdinalIgnoreCase),
        };

        return new CompiledIvrStage
        {
            Id = stage.Id,
            Description = stage.Description,
            Goal = stage.Goal,
            Terminal = stage.Terminal,
            Strategy = stagePolicy,
            Tools = resolvedTools,
            Capabilities = capabilityIds,
            Intents = compiledIntents,
            RuntimeStep = runtimeStep,
        };
    }

    private IReadOnlyDictionary<string, CompiledIvrCapability> CompileCapabilities(
        IvrWorkflowDocument document,
        List<string> errors)
    {
        var result = new Dictionary<string, CompiledIvrCapability>(StringComparer.Ordinal);
        foreach (var cap in document.Capabilities)
        {
            if (string.IsNullOrWhiteSpace(cap.Id))
            {
                continue;
            }
            var guards = new List<IIvrStepGuard>();
            foreach (var g in cap.Requires)
            {
                try
                {
                    guards.Add(BuildGuard(document.Name, $"capability:{cap.Id}", g));
                }
                catch (InvalidOperationException ex)
                {
                    errors.Add(ex.Message);
                }
            }
            var tools = ResolveTools(cap.Tools, errors, $"capability '{cap.Id}'");

            result[cap.Id] = new CompiledIvrCapability
            {
                Id = cap.Id,
                Description = cap.Description,
                Tools = tools,
                Guards = guards,
            };
        }
        return result;
    }

    private List<AITool> ResolveTools(IEnumerable<string> names, List<string> errors, string context)
    {
        var resolved = new List<AITool>();
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            var tool = _tools.Resolve(name);
            if (tool is null)
            {
                errors.Add($"Unresolved tool '{name}' referenced by {context}.");
                continue;
            }
            resolved.Add(tool);
        }
        return resolved;
    }

    private IIvrStepGuard BuildGuard(string workflowName, string? stageId, IvrGuardDocument doc)
    {
        if (!_guardFactories.TryGetValue(doc.Type, out var factory))
        {
            throw new InvalidOperationException(
                $"Workflow '{workflowName}' stage '{stageId ?? "(global)"}': no guard factory registered for type '{doc.Type}'.");
        }
        var ctx = new GuardBuildContext(workflowName, stageId, _predicates);
        return factory.Create(doc, ctx);
    }

    private static CallerVerificationLevel ResolveStageAuthLevel(CallerVerificationLevel baseLevel, IEnumerable<IvrGuardDocument> requires)
    {
        var level = baseLevel;
        foreach (var guard in requires)
        {
            if (string.Equals(guard.Type, "auth", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = ParseAuthLevel(guard.Level);
                if (parsed > level)
                {
                    level = parsed;
                }
            }
        }
        return level;
    }

    private static IReadOnlyList<StateTransition>? BuildTransitions(IvrStageDocument stage)
    {
        var transitions = new List<StateTransition>();

        if (stage.OnExit is { Length: > 0 } onExit)
        {
            transitions.Add(new StateTransition
            {
                Condition = stage.ExitWhen ?? "default",
                NextStep = onExit,
            });
        }

        foreach (var t in stage.Transitions)
        {
            if (string.IsNullOrWhiteSpace(t.To))
            {
                continue;
            }
            var condition = t.OnIntent is { Length: > 0 } i ? $"intent:{i}"
                : t.OnCondition is { Length: > 0 } c ? c
                : "default";
            transitions.Add(new StateTransition
            {
                Condition = condition,
                NextStep = t.To,
            });
        }

        foreach (var intent in stage.Intents)
        {
            if (intent.NextStage is { Length: > 0 } next)
            {
                transitions.Add(new StateTransition
                {
                    Condition = $"intent:{intent.Name}",
                    NextStep = next,
                });
            }
        }

        return transitions.Count == 0 ? null : transitions;
    }

    /// <summary>
    /// Phase 2: resolve an <see cref="IvrStageImportDocument"/> against the catalog,
    /// validate the source stage is a leaf, and clone its compiled form under the local
    /// alias. Tools, prompts, guards, and scripted blocks travel unchanged (tool
    /// instances are shared singletons via <see cref="IIvrToolRegistry"/>).
    /// </summary>
    private CompiledIvrStage CompileImportStage(
        IvrStageDocument stage,
        IvrStageImportDocument import,
        IvrStrategyPolicy workflowPolicy,
        List<string> errors)
    {
        // import.stage accepts two forms (resolved via a catalog-aware longest-prefix match):
        //   1) Bare workflow id — used when the referenced workflow has exactly one stage.
        //   2) "workflowId.stageId" — workflow ids may themselves contain dots
        //      (e.g. "banking.lib.closing" => workflow "banking.lib", stage "closing").
        // The right-to-left longest-prefix scan asks the catalog which prefix it knows,
        // so users don't have to disambiguate manually.
        var reference = (import.Stage ?? string.Empty).Trim();
        // Effective id used in diagnostics so we never emit "Stage '': ..." when the
        // import stage has no top-level id (the YAML uses `as:` instead).
        string EffectiveId(string? srcWorkflowId = null, string? srcStageId = null) =>
            !string.IsNullOrWhiteSpace(import.As) ? import.As!
            : !string.IsNullOrWhiteSpace(stage.Id) ? stage.Id
            : !string.IsNullOrWhiteSpace(srcStageId) ? srcStageId!
            : !string.IsNullOrWhiteSpace(srcWorkflowId) ? srcWorkflowId!
            : "(import)";

        if (reference.Length == 0)
        {
            errors.Add($"Stage '{EffectiveId()}': import.stage is required.");
            return BuildPlaceholderImport(stage, workflowPolicy);
        }
        if (_catalogAccessor is null)
        {
            errors.Add(
                $"Stage '{EffectiveId()}': stage imports require an IIvrWorkflowCatalog. " +
                "Construct IvrWorkflowCompiler with the catalogAccessor argument.");
            return BuildPlaceholderImport(stage, workflowPolicy);
        }
        var catalog = _catalogAccessor();

        string? sourceWorkflowId = null;
        string? sourceStageId = null;

        // (1) Whole reference IS a workflow id (covers bare ids and multi-segment ids
        //     like "subflows.closing" where the whole thing is the workflow name).
        if (catalog.TryGet(reference, import.MinVersion, import.MaxVersion, out _))
        {
            sourceWorkflowId = reference;
        }
        else
        {
            // (2) Longest-prefix match across dots, right-to-left.
            for (var i = reference.LastIndexOf('.'); i > 0; i = reference.LastIndexOf('.', i - 1))
            {
                var prefix = reference[..i];
                var suffix = reference[(i + 1)..];
                if (suffix.Length == 0) { continue; }
                if (catalog.TryGet(prefix, import.MinVersion, import.MaxVersion, out _))
                {
                    sourceWorkflowId = prefix;
                    sourceStageId = suffix;
                    break;
                }
            }
        }

        if (sourceWorkflowId is null)
        {
            var pinSuffix = (import.MinVersion is not null || import.MaxVersion is not null)
                ? $" (minVersion={import.MinVersion?.ToString(CultureInfo.InvariantCulture) ?? "*"}, maxVersion={import.MaxVersion?.ToString(CultureInfo.InvariantCulture) ?? "*"})"
                : string.Empty;
            errors.Add(
                $"Stage '{EffectiveId()}': import.stage '{reference}' could not be resolved to a known workflow. " +
                $"Check the workflow id and any minVersion/maxVersion pins{pinSuffix}.");
            return BuildPlaceholderImport(stage, workflowPolicy);
        }

        var localId = EffectiveId(sourceWorkflowId, sourceStageId);

        CompiledIvrWorkflow sourceWorkflow;
        try
        {
            sourceWorkflow = catalog.Get(sourceWorkflowId, import.MinVersion, import.MaxVersion);
        }
        catch (KeyNotFoundException ex)
        {
            errors.Add($"Stage '{localId}': {ex.Message}");
            return BuildPlaceholderImport(stage, workflowPolicy, localId);
        }

        CompiledIvrStage? sourceStage;
        if (sourceStageId is not null)
        {
            sourceStage = sourceWorkflow.Stages.FirstOrDefault(
                s => string.Equals(s.Id, sourceStageId, StringComparison.Ordinal));
        }
        else if (sourceWorkflow.Stages.Count == 1)
        {
            sourceStage = sourceWorkflow.Stages[0];
            sourceStageId = sourceStage.Id;
            // Recompute the effective id now that we know the source stage.
            localId = EffectiveId(sourceWorkflowId, sourceStageId);
        }
        else
        {
            errors.Add(
                $"Stage '{localId}': import.stage '{reference}' resolved to workflow '{sourceWorkflowId}' v{sourceWorkflow.Version} which has {sourceWorkflow.Stages.Count} stages; " +
                $"specify the stage id explicitly (e.g. '{reference}.<stageId>').");
            return BuildPlaceholderImport(stage, workflowPolicy, localId);
        }

        if (sourceStage is null)
        {
            errors.Add(
                $"Stage '{localId}': import.stage references stage '{sourceStageId}' which does not exist in workflow '{sourceWorkflowId}' v{sourceWorkflow.Version}.");
            return BuildPlaceholderImport(stage, workflowPolicy, localId);
        }

        var sourceRuntime = sourceStage.RuntimeStep;
        if (sourceRuntime is SubflowIvrWorkflowStep)
        {
            errors.Add(
                $"Stage '{localId}': cannot import subflow-marker stage '{sourceWorkflowId}.{sourceStageId}'. Reference the workflow directly via 'type: subflow' instead.");
            return BuildPlaceholderImport(stage, workflowPolicy, localId);
        }

        var outboundTransitions = sourceRuntime.ConversationState.Transitions?.Count ?? 0;
        if (outboundTransitions > 0)
        {
            errors.Add(
                $"Stage '{localId}': cannot import stage '{sourceWorkflowId}.{sourceStageId}' because it declares {outboundTransitions} outbound transition(s). " +
                "Only leaf stages are importable in Phase 2; use 'type: subflow' to delegate into a workflow you can return from.");
            return BuildPlaceholderImport(stage, workflowPolicy, localId);
        }

        // Deep-clone the runtime step under the local id. ConversationState.Id mirrors
        // the local id so the rendered prompt's "Stage:" header reflects the alias.
        var clonedConversationState = sourceRuntime.ConversationState with { Id = localId };
        var clonedRuntime = new RealtimeIvrWorkflowStep
        {
            Id = localId,
            ConversationState = clonedConversationState,
            AvailableTools = sourceRuntime.AvailableTools,
            ToolRules = sourceRuntime.ToolRules,
            Guards = sourceRuntime.Guards,
            Validators = sourceRuntime.Validators,
            RequiredStateKeys = sourceRuntime.RequiredStateKeys,
            MaxRetries = sourceRuntime.MaxRetries,
            MaxDuration = sourceRuntime.MaxDuration,
            RequiredAuthLevel = sourceRuntime.RequiredAuthLevel,
            OnCompleted = sourceRuntime.OnCompleted,
            StepScriptedConfiguration = sourceRuntime.StepScriptedConfiguration,
            Terminal = sourceRuntime.Terminal,
            TerminalOutcome = sourceRuntime.TerminalOutcome,
            Intents = sourceRuntime.Intents,
        };

        return new CompiledIvrStage
        {
            Id = localId,
            Description = sourceStage.Description,
            Goal = sourceStage.Goal,
            Terminal = sourceStage.Terminal,
            Strategy = workflowPolicy,
            Tools = sourceStage.Tools,
            Capabilities = sourceStage.Capabilities,
            Intents = sourceStage.Intents,
            RuntimeStep = clonedRuntime,
        };
    }

    /// <summary>
    /// Build a degenerate <see cref="CompiledIvrStage"/> used when an import fails. The
    /// compiler will throw with the accumulated errors anyway; this just lets the rest
    /// of the document continue to compile so all errors surface at once.
    /// </summary>
    private static CompiledIvrStage BuildPlaceholderImport(
        IvrStageDocument stage,
        IvrStrategyPolicy workflowPolicy,
        string? localId = null)
    {
        var id = !string.IsNullOrWhiteSpace(localId) ? localId!
            : !string.IsNullOrWhiteSpace(stage.Id) ? stage.Id
            : "import-error";
        var runtimeStep = new RealtimeIvrWorkflowStep
        {
            Id = id,
            ConversationState = new ConversationState { Id = id, Description = id, Instructions = [] },
        };
        return new CompiledIvrStage
        {
            Id = id,
            Description = stage.Description,
            Goal = stage.Goal,
            Terminal = stage.Terminal,
            Strategy = workflowPolicy,
            Tools = [],
            Capabilities = [],
            Intents = new Dictionary<string, CompiledIvrIntent>(StringComparer.OrdinalIgnoreCase),
            RuntimeStep = runtimeStep,
        };
    }

    /// <summary>
    /// Lower a YAML stage tagged <c>type: subflow</c> into a marker
    /// <see cref="SubflowIvrWorkflowStep"/>. The realtime navigator detects the marker
    /// at entry time and pushes the referenced child workflow onto its frame stack
    /// instead of rendering a prompt.
    /// </summary>
    private static CompiledIvrStage CompileSubflowStage(
        IvrStageDocument stage,
        IvrStrategyPolicy workflowPolicy,
        List<string> errors)
    {
        var subflowDoc = stage.Subflow;
        var workflowId = subflowDoc?.WorkflowId;
        if (string.IsNullOrWhiteSpace(workflowId))
        {
            errors.Add($"Stage '{stage.Id}': type=subflow requires 'subflow.workflowId'.");
            workflowId = string.Empty;
        }

        // Stage-level shortcut fields take precedence over the nested subflow block.
        var onSuccess = !string.IsNullOrWhiteSpace(stage.OnSuccess) ? stage.OnSuccess : subflowDoc?.OnSuccess;
        var onFailure = !string.IsNullOrWhiteSpace(stage.OnFailure) ? stage.OnFailure : subflowDoc?.OnFailure;

        // Synthesize transitions so the navigator's TransitionTo (which validates against
        // CurrentStep.ValidTransitions) accepts the resume targets emitted by PopFrameAsync.
        var transitions = new List<StateTransition>();
        if (!string.IsNullOrWhiteSpace(onSuccess))
        {
            transitions.Add(new StateTransition { Condition = "subflow:onSuccess", NextStep = onSuccess! });
        }
        if (!string.IsNullOrWhiteSpace(onFailure))
        {
            transitions.Add(new StateTransition { Condition = "subflow:onFailure", NextStep = onFailure! });
        }

        var conversationState = new ConversationState
        {
            Id = stage.Id,
            Description = stage.Description ?? $"Sub-workflow: {workflowId}",
            Goal = stage.Goal,
            Instructions = (IReadOnlyList<string>)[],
            ExitWhen = stage.ExitWhen,
            Transitions = transitions.Count == 0 ? null : transitions,
        };

        var runtimeStep = new SubflowIvrWorkflowStep
        {
            Id = stage.Id,
            ConversationState = conversationState,
            SubflowWorkflowId = workflowId ?? string.Empty,
            OnSuccessStepId = onSuccess,
            OnFailureStepId = onFailure,
            MinVersion = subflowDoc?.MinVersion,
            MaxVersion = subflowDoc?.MaxVersion,
            Terminal = stage.Terminal,
            TerminalOutcome = ParseTerminalOutcome(stage.TerminalOutcome),
            MaxRetries = stage.MaxRetries ?? 3,
        };

        return new CompiledIvrStage
        {
            Id = stage.Id,
            Description = stage.Description,
            Goal = stage.Goal,
            Terminal = stage.Terminal,
            Strategy = workflowPolicy,
            Tools = [],
            Capabilities = [],
            Intents = new Dictionary<string, CompiledIvrIntent>(StringComparer.OrdinalIgnoreCase),
            RuntimeStep = runtimeStep,
        };
    }

    private static TerminalOutcome ParseTerminalOutcome(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TerminalOutcome.Success;
        }
        return value.Trim().ToLowerInvariant() switch
        {
            "success" or "ok" or "completed" => TerminalOutcome.Success,
            "failure" or "failed" or "cancel" or "cancelled" => TerminalOutcome.Failure,
            _ => TerminalOutcome.Success,
        };
    }

    /// <summary>
    /// Phase 3: lower per-transition <c>requires:</c> into compiled
    /// <see cref="TransitionRule"/> records. The same transition is also represented as a
    /// <c>ConversationState.Transitions</c> entry (for legacy consumers that only need
    /// the target id); this list adds the guard metadata the navigator's auth-resolver
    /// detour needs.
    /// </summary>
    private IReadOnlyList<TransitionRule> BuildTransitionRules(string workflowName, IvrStageDocument stage, List<string> errors)
    {
        if (stage.Transitions.Count == 0 && string.IsNullOrWhiteSpace(stage.OnExit))
        {
            return [];
        }

        var rules = new List<TransitionRule>();

        if (stage.OnExit is { Length: > 0 } onExit)
        {
            rules.Add(new TransitionRule
            {
                TargetStepId = onExit,
                Condition = stage.ExitWhen ?? "default",
                Guards = [],
            });
        }

        foreach (var t in stage.Transitions)
        {
            if (string.IsNullOrWhiteSpace(t.To))
            {
                continue;
            }

            IReadOnlyList<IIvrStepGuard> tGuards = [];
            if (t.Requires.Count > 0)
            {
                var built = new List<IIvrStepGuard>(t.Requires.Count);
                foreach (var g in t.Requires)
                {
                    try { built.Add(BuildGuard(workflowName, stage.Id, g)); }
                    catch (InvalidOperationException ex) { errors.Add(ex.Message); }
                }
                tGuards = built;
            }

            rules.Add(new TransitionRule
            {
                TargetStepId = t.To,
                Condition = t.OnIntent is { Length: > 0 } i ? $"intent:{i}"
                    : t.OnCondition is { Length: > 0 } c ? c
                    : "default",
                Guards = tGuards,
            });
        }

        return rules;
    }

    /// <summary>
    /// Phase 3: lower the workflow-level <c>authResolvers:</c> list into runtime
    /// <see cref="CompiledAuthResolver"/> entries. Each entry pairs a guard-shape matcher
    /// with the sub-workflow to push when a transition's guard fails.
    /// </summary>
    private IReadOnlyList<CompiledAuthResolver> CompileAuthResolvers(IvrWorkflowDocument document, List<string> errors)
    {
        if (document.AuthResolvers.Count == 0)
        {
            return [];
        }

        var resolvers = new List<CompiledAuthResolver>(document.AuthResolvers.Count);
        foreach (var r in document.AuthResolvers)
        {
            if (string.IsNullOrWhiteSpace(r.Subflow))
            {
                errors.Add($"Workflow '{document.Name}': authResolver entry is missing 'subflow'.");
                continue;
            }

            IIvrStepGuard template;
            try
            {
                template = BuildGuard(document.Name, stageId: null, r.Guard);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(ex.Message);
                continue;
            }

            // Shape-based matcher: for known built-in guards we compare the discriminating
            // field (level, etc.); for everything else we fall back to runtime-type
            // equality. Custom guard kinds can register their own matchers later by
            // implementing equality on the produced IIvrStepGuard.
            var matcher = template switch
            {
                Guards.RequiredAuthLevelGuard authTemplate => new Func<IIvrStepGuard, bool>(g =>
                    g is Guards.RequiredAuthLevelGuard candidate && candidate.RequiredLevel == authTemplate.RequiredLevel),
                _ => (Func<IIvrStepGuard, bool>)(g => g.GetType() == template.GetType()),
            };

            var description = template is Guards.RequiredAuthLevelGuard authT
                ? $"auth:{authT.RequiredLevel}"
                : template.GetType().Name;

            resolvers.Add(new CompiledAuthResolver
            {
                Matches = matcher,
                SubflowWorkflowId = r.Subflow,
                MinVersion = r.MinVersion,
                MaxVersion = r.MaxVersion,
                Description = description,
            });
        }
        return resolvers;
    }

    private static CallerVerificationLevel ParseAuthLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return CallerVerificationLevel.None;
        }
        return value.Trim().ToLowerInvariant() switch
        {
            "none" => CallerVerificationLevel.None,
            "animatch" or "ani" or "phone" => CallerVerificationLevel.AniMatch,
            "knowledgebased" or "knowledge" or "kba" or "pin" => CallerVerificationLevel.KnowledgeBased,
            "multifactor" or "mfa" => CallerVerificationLevel.MultiFactor,
            "voicebiometric" or "biometric" or "voice" => CallerVerificationLevel.VoiceBiometric,
            "entraverifiedid" or "verifiedid" => CallerVerificationLevel.EntraVerifiedId,
            "strong" => CallerVerificationLevel.Strong,
            _ => CallerVerificationLevel.None,
        };
    }

    private static TimeSpan? ParseDuration(string? value, string stageId, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var ts))
        {
            return ts;
        }
        errors.Add($"Stage '{stageId}': could not parse maxDuration '{value}'. Use TimeSpan format (e.g., 00:02:30).");
        return null;
    }

    private static IvrStrategyPolicy? MapStrategy(IvrStrategyDocument? strategy)
    {
        if (strategy is null)
        {
            return null;
        }
        var primary = IvrInteractionModeMappings.TryParse(strategy.Primary)
            ?? throw new InvalidOperationException($"Unrecognized strategy.primary value '{strategy.Primary}'.");
        var fallback = strategy.Fallback
            .Select(m => IvrInteractionModeMappings.TryParse(m)
                ?? throw new InvalidOperationException($"Unrecognized strategy.fallback value '{m}'."))
            .ToList();
        var prewarm = strategy.PrewarmTiers
            .Select(m => IvrInteractionModeMappings.TryParse(m)
                ?? throw new InvalidOperationException($"Unrecognized strategy.prewarmTiers value '{m}'."))
            .ToList();
        return new IvrStrategyPolicy(primary, fallback, prewarm, strategy.AllowMidCallDegradation);
    }

    private sealed record GuardBuildContext(string WorkflowName, string? StageId, IIvrPredicateRegistry Predicates)
        : IIvrGuardBuildContext;
}
