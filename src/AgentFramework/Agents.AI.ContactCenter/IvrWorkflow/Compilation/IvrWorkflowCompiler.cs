using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    public IvrWorkflowCompiler(
        IIvrToolRegistry tools,
        IIvrPredicateRegistry predicates,
        IEnumerable<IIvrGuardFactory>? guardFactories = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(predicates);
        _tools = tools;
        _predicates = predicates;
        var factories = (guardFactories ?? BuiltInGuardFactories.CreateAll())
            .GroupBy(f => f.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        _guardFactories = factories;
    }

    public CompiledIvrWorkflow Compile(IvrWorkflowDocument document, IvrWorkflowSourceEntry? source = null)
    {
        ArgumentNullException.ThrowIfNull(document);
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

        var runtime = new RealtimeIvrWorkflowDefinition
        {
            Name = document.Name,
            BasePrompt = basePrompt,
            Steps = runtimeSteps,
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
        AuthenticationLevel baseRequiredAuth,
        IReadOnlyDictionary<string, CompiledIvrCapability> capabilities,
        IvrStrategyPolicy workflowPolicy,
        Dictionary<string, List<string>> intentExamples,
        List<string> errors)
    {
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
        if (baseRequiredAuth > AuthenticationLevel.None)
        {
            guards.Add(new Guards.RequiredAuthLevelGuard(baseRequiredAuth));
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

        // DTMF compile.
        StepDtmfConfiguration? dtmfConfig = null;
        if (stage.Dtmf is { } dtmfDoc)
        {
            dtmfConfig = IvrDtmfMapper.Map(dtmfDoc, stage.Id, errors);
        }

        // ConversationState (instructions/examples/exit/transitions).
        var instructions = stage.Realtime?.Instructions.Count > 0
            ? stage.Realtime.Instructions
            : (stage.Goal is { Length: > 0 } goalText ? new[] { goalText } : (IReadOnlyList<string>)[]);

        var transitions = BuildTransitions(stage);

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
            StepDtmfConfiguration = dtmfConfig,
            Terminal = stage.Terminal,
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

    private static AuthenticationLevel ResolveStageAuthLevel(AuthenticationLevel baseLevel, IEnumerable<IvrGuardDocument> requires)
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

    private static AuthenticationLevel ParseAuthLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AuthenticationLevel.None;
        }
        return value.Trim().ToLowerInvariant() switch
        {
            "none" => AuthenticationLevel.None,
            "phonerecognized" or "phone" or "ani" => AuthenticationLevel.PhoneRecognized,
            "accountverified" or "account" => AuthenticationLevel.AccountVerified,
            "securityquestionpassed" or "security" => AuthenticationLevel.SecurityQuestionPassed,
            "fullyauthenticated" or "mfa" or "full" => AuthenticationLevel.FullyAuthenticated,
            _ => AuthenticationLevel.None,
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
