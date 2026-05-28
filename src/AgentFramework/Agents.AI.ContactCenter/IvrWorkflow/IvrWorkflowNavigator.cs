using System.Diagnostics.CodeAnalysis;
using System.Text;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <inheritdoc cref="IIvrWorkflowNavigator"/>
public sealed class IvrWorkflowNavigator : IIvrWorkflowNavigator
{
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private readonly IIvrWorkflowCatalog _catalog;

    /// <summary>
    /// Single-definition constructor. Wraps <paramref name="definition"/> in an in-memory
    /// catalog so callers that don't have a real <see cref="IIvrWorkflowCatalog"/>
    /// (legacy strategies, tests) continue to work; subflow stages reached via this
    /// constructor will fail on push because no other workflows are registered.
    /// </summary>
    public IvrWorkflowNavigator(
        RealtimeIvrWorkflowDefinition definition,
        IvrWorkflowState state,
        IServiceProvider services,
        ILogger<IvrWorkflowNavigator>? logger = null)
        : this(definition, state, services, new SingleDefinitionCatalog(definition), logger)
    {
    }

    /// <summary>
    /// Catalog-aware constructor. The navigator can resolve any frame's workflow
    /// through <paramref name="catalog"/>, enabling Phase 1 subflow push/pop.
    /// </summary>
    public IvrWorkflowNavigator(
        RealtimeIvrWorkflowDefinition definition,
        IvrWorkflowState state,
        IServiceProvider services,
        IIvrWorkflowCatalog catalog,
        ILogger<IvrWorkflowNavigator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(catalog);

        Definition = definition;
        State = state;
        _services = services;
        _catalog = catalog;
        _logger = logger ?? NullLogger<IvrWorkflowNavigator>.Instance;
    }

    /// <summary>The root workflow this navigator was constructed with. The active frame may belong to a different workflow once subflows have been pushed.</summary>
    public RealtimeIvrWorkflowDefinition Definition { get; }

    public IvrWorkflowState State { get; }

    /// <summary>Resolves the workflow definition matching the current frame; falls back to <see cref="Definition"/> when no frame is active or the catalog can't resolve it.</summary>
    private RealtimeIvrWorkflowDefinition ActiveDefinition
    {
        get
        {
            var frame = State.CurrentFrame;
            if (frame is null || string.IsNullOrEmpty(frame.WorkflowId))
            {
                return Definition;
            }
            if (string.Equals(frame.WorkflowId, Definition.Name, StringComparison.Ordinal))
            {
                return Definition;
            }
            return _catalog.TryGet(frame.WorkflowId, out var compiled)
                ? compiled!.Runtime
                : Definition;
        }
    }

    public RealtimeIvrWorkflowStep? CurrentStep
    {
        get
        {
            var frame = State.CurrentFrame;
            if (frame is null || string.IsNullOrEmpty(frame.CurrentStepId))
            {
                return null;
            }
            return ActiveDefinition.GetStep(frame.CurrentStepId);
        }
    }

    public RealtimeIvrWorkflowStep EnterInitialStep()
    {
        var stepId = State.CurrentStepName ?? Definition.InitialStepId;
        var step = Definition.GetStep(stepId)
            ?? throw new InvalidOperationException($"Step '{stepId}' not found in workflow '{Definition.Name}'.");

        var stepIndex = Definition.GetStepIndex(step.Id);
        var startedAt = DateTimeOffset.UtcNow;

        // Push a real frame if none exists yet (or the top frame is the anonymous shim
        // frame produced by a legacy direct write to CurrentStepName). Resume-after-tier-swap:
        // if the top frame already belongs to this workflow, keep it as-is so we don't
        // reset StepStartedAt or clobber sub-frames that survived the swap.
        var current = State.CurrentFrame;
        if (current is null || !string.Equals(current.WorkflowId, Definition.Name, StringComparison.Ordinal))
        {
            if (current is not null && current.WorkflowId.Length == 0)
            {
                State.PopFrame();
            }
            State.PushFrame(new WorkflowFrame
            {
                WorkflowId = Definition.Name,
                CurrentStepId = step.Id,
                CurrentStepIndex = stepIndex,
                StepStartedAt = startedAt,
            });
        }
        else
        {
            current.CurrentStepId = step.Id;
            current.CurrentStepIndex = stepIndex;
            current.StepStartedAt ??= startedAt;
        }

        if (State.Status is IvrWorkflowStatus.NotStarted)
        {
            State.Status = IvrWorkflowStatus.Running;
        }

        return step;
    }

    public TransitionResult TransitionTo(string targetStepId)
    {
        var current = CurrentStep;
        if (current is null)
        {
            return TransitionResult.Invalid("no current step");
        }

        if (!current.ValidTransitions.Contains(targetStepId, StringComparer.Ordinal))
        {
            return TransitionResult.Invalid(
                $"'{targetStepId}' is not a valid transition from '{current.Id}'");
        }

        var active = ActiveDefinition;
        var target = active.GetStep(targetStepId);
        if (target is null)
        {
            return TransitionResult.Unknown($"step '{targetStepId}' not in workflow '{active.Name}'");
        }

        State.MarkStepCompleted(current.Id);
        State.CurrentStepName = target.Id;
        State.CurrentStepIndex = active.GetStepIndex(target.Id);
        State.StepStartedAt = DateTimeOffset.UtcNow;
        return TransitionResult.Success(target);
    }

    public async Task<TransitionEvaluation> EvaluateTransitionAsync(
        string targetStepId,
        CancellationToken cancellationToken = default)
    {
        var current = CurrentStep;
        if (current is null)
        {
            return new TransitionEvaluation.Invalid("no current step");
        }
        if (!current.ValidTransitions.Contains(targetStepId, StringComparer.Ordinal))
        {
            return new TransitionEvaluation.Invalid(
                $"'{targetStepId}' is not a valid transition from '{current.Id}'");
        }

        var active = ActiveDefinition;
        var target = active.GetStep(targetStepId);
        if (target is null)
        {
            return new TransitionEvaluation.Invalid(
                $"step '{targetStepId}' not in workflow '{active.Name}'");
        }

        // Per-transition guards (from the YAML transition's `requires:`) followed by the
        // target stage's own entry guards. Evaluated in order — first failure wins.
        var transitionGuards = current.TransitionRules
            .FirstOrDefault(r => string.Equals(r.TargetStepId, targetStepId, StringComparison.Ordinal))?
            .Guards ?? [];

        foreach (var guard in transitionGuards.Concat(target.Guards))
        {
            var result = await guard.EvaluateAsync(State, cancellationToken).ConfigureAwait(false);
            if (result.Passed)
            {
                continue;
            }

            // First failure — try to resolve via the workflow's authResolvers.
            var resolver = active.AuthResolvers.FirstOrDefault(r => r.Matches(guard));
            if (resolver is null)
            {
                return new TransitionEvaluation.BlockedNoResolver(
                    result.FailureReason ?? "Transition blocked by an unsatisfied guard.",
                    guard);
            }

            return new TransitionEvaluation.RequiresDetour(
                target,
                resolver.SubflowWorkflowId,
                resolver.MinVersion,
                resolver.MaxVersion,
                guard,
                resolver.Description);
        }

        return new TransitionEvaluation.Allowed(target);
    }

    public async Task<RealtimeIvrWorkflowStep> PushSubflowAsync(
        string workflowId,
        string? returnToStepId,
        string? failureReturnStepId,
        int? minVersion = null,
        int? maxVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);

        // Ensure the catalog has discovered the requested workflow at least once. Cheap
        // when already loaded; on first reference this pulls the YAML from disk.
        await _catalog.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        if (!_catalog.TryGet(workflowId, minVersion, maxVersion, out var compiled))
        {
            throw new KeyNotFoundException(
                $"Cannot push subflow '{workflowId}' (min={minVersion?.ToString() ?? "-"}, max={maxVersion?.ToString() ?? "-"}): no matching version is registered.");
        }

        // Cycle detection: the same workflow id must not appear lower on the stack.
        foreach (var existing in State.Frames)
        {
            if (string.Equals(existing.WorkflowId, workflowId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Subflow '{workflowId}' is already on the frame stack; cyclic subflow references are not supported.");
            }
        }

        // Mark the parent's current step (the subflow marker itself) completed so
        // PreviousStepCompletedGuard semantics across the boundary stay sensible.
        if (State.CurrentFrame is { CurrentStepId.Length: > 0 } parent)
        {
            State.MarkStepCompleted(parent.CurrentStepId);
        }

        var childDefinition = compiled.Runtime;
        var initialStep = childDefinition.GetStep(childDefinition.InitialStepId)
            ?? throw new InvalidOperationException(
                $"Subflow '{workflowId}' has no resolvable initial step '{childDefinition.InitialStepId}'.");

        State.PushFrame(new WorkflowFrame
        {
            WorkflowId = workflowId,
            WorkflowVersion = compiled.Version >= 1 ? compiled.Version : 1,
            CurrentStepId = initialStep.Id,
            CurrentStepIndex = childDefinition.GetStepIndex(initialStep.Id),
            StepStartedAt = DateTimeOffset.UtcNow,
            ReturnToStepId = returnToStepId,
            FailureReturnStepId = failureReturnStepId,
        });

        _logger.LogInformation(
            "Pushed subflow '{WorkflowId}' v{Version} (depth={Depth}); initial step '{StepId}'. returnTo='{ReturnTo}' failureReturnTo='{FailureReturnTo}'",
            workflowId, compiled.Version, State.FrameDepth, initialStep.Id, returnToStepId, failureReturnStepId);

        return initialStep;
    }

    public async Task<RealtimeIvrWorkflowStep?> PopFrameAsync(bool success, CancellationToken cancellationToken = default)
    {
        var popped = State.PopFrame();
        if (popped is null)
        {
            return null;
        }

        // Mark the popped frame's current step (a terminal child stage) completed.
        if (popped.CurrentStepId.Length > 0)
        {
            State.MarkStepCompleted(popped.CurrentStepId);
        }

        var parent = State.CurrentFrame;
        if (parent is null)
        {
            // No parent → the root workflow completed.
            Complete(success ? IvrWorkflowStatus.Completed : IvrWorkflowStatus.Failed);
            _logger.LogInformation(
                "Popped root frame '{WorkflowId}' (success={Success}); workflow complete.",
                popped.WorkflowId, success);
            return null;
        }

        var returnToStepId = success ? popped.ReturnToStepId : popped.FailureReturnStepId;
        if (string.IsNullOrEmpty(returnToStepId))
        {
            _logger.LogInformation(
                "Popped subflow '{WorkflowId}' (success={Success}) but no return step is set; parent stays on '{ParentStepId}'.",
                popped.WorkflowId, success, parent.CurrentStepId);
            return CurrentStep;
        }

        var parentDefinition = string.Equals(parent.WorkflowId, Definition.Name, StringComparison.Ordinal)
            ? Definition
            : _catalog.TryGet(parent.WorkflowId, out var compiled)
                ? compiled!.Runtime
                : Definition;

        var resumed = parentDefinition.GetStep(returnToStepId)
            ?? throw new InvalidOperationException(
                $"Parent workflow '{parent.WorkflowId}' has no step '{returnToStepId}' to resume on after subflow pop.");

        // Update the parent frame to point at the resume step. We bypass the TransitionTo
        // validation here because the resume target was authored as the subflow stage's
        // onSuccess/onFailure (the navigator owns that contract, not the YAML transitions).
        parent.CurrentStepId = resumed.Id;
        parent.CurrentStepIndex = parentDefinition.GetStepIndex(resumed.Id);
        parent.StepStartedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "Popped subflow '{WorkflowId}' (success={Success}); resumed parent '{ParentWorkflow}' at step '{StepId}'.",
            popped.WorkflowId, success, parent.WorkflowId, resumed.Id);

        // Phase 3 chained-detour: re-evaluate the resume target's stage-level guards
        // against the (now mutated) state. If a guard still fails and the parent
        // workflow has a matching auth-resolver, push another subflow with the same
        // returnToStepId so the chain can be completed automatically (e.g. PIN → OTP for
        // an MFA-gated balance stage). Phase 1 cycle detection on PushSubflowAsync
        // prevents infinite chains by refusing to push a workflow already on the stack.
        if (success && resumed.Guards.Count > 0)
        {
            foreach (var guard in resumed.Guards)
            {
                var gr = await guard.EvaluateAsync(State, cancellationToken).ConfigureAwait(false);
                if (gr.Passed)
                {
                    continue;
                }

                var resolver = parentDefinition.AuthResolvers.FirstOrDefault(r => r.Matches(guard));
                if (resolver is null)
                {
                    // Honor the parent's onUnauthorized fallback when set; otherwise we
                    // simply enter the target step and let the per-tool guards reject.
                    var fallbackId = resumed.OnUnauthorizedStepId
                        ?? parentDefinition.UnauthorizedFailureStepId;
                    if (!string.IsNullOrEmpty(fallbackId) &&
                        parentDefinition.GetStep(fallbackId) is { } fallbackStep)
                    {
                        parent.CurrentStepId = fallbackStep.Id;
                        parent.CurrentStepIndex = parentDefinition.GetStepIndex(fallbackStep.Id);
                        parent.StepStartedAt = DateTimeOffset.UtcNow;
                        _logger.LogInformation(
                            "Resume target '{Step}' still gated by '{Guard}' after detour; routing to onUnauthorized '{Fallback}'.",
                            resumed.Id, guard.GetType().Name, fallbackStep.Id);
                        return fallbackStep;
                    }
                    return resumed;
                }

                _logger.LogInformation(
                    "Resume target '{Step}' still gated by '{Guard}'; chaining detour through '{Resolver}' ({Subflow}).",
                    resumed.Id, guard.GetType().Name, resolver.Description, resolver.SubflowWorkflowId);

                return await PushSubflowAsync(
                    resolver.SubflowWorkflowId,
                    returnToStepId: resumed.Id,
                    failureReturnStepId: resumed.OnUnauthorizedStepId ?? parentDefinition.UnauthorizedFailureStepId,
                    resolver.MinVersion,
                    resolver.MaxVersion,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return resumed;
    }

    public void Complete(IvrWorkflowStatus status = IvrWorkflowStatus.Completed)
    {
        if (CurrentStep is { } step)
        {
            State.MarkStepCompleted(step.Id);
        }
        State.Status = status;
    }

    public bool TryResolveDtmfDigit(char digit, [NotNullWhen(true)] out DtmfMenuOption? option)
    {
        option = null;
        return CurrentStep?.StepScriptedConfiguration?.Dtmf?.MenuOptions is { } menu
            && menu.TryGetValue(digit, out option);
    }

    public ValueTask<DtmfActionResult> InvokeMenuActionAsync(
        DtmfMenuOption option,
        IReadOnlyDictionary<string, object?>? extraArguments,
        CancellationToken cancellationToken)
    {
        if (option.ActionToolName is null)
        {
            return ValueTask.FromResult<DtmfActionResult>(
                option.NextStepId is { } next
                    ? new DtmfActionResult.Transition(next)
                    : new DtmfActionResult.Repeat());
        }

        var step = CurrentStep
            ?? throw new InvalidOperationException("No current step; cannot resolve menu action.");

        var tool = step.AvailableTools?.FirstOrDefault(
            t => string.Equals(t.Name, option.ActionToolName, StringComparison.Ordinal));

        if (tool is null)
        {
            _logger.LogWarning(
                "DTMF option for digit '{Digit}' references tool '{ToolName}', which is not present in step '{StepId}' AvailableTools.",
                option.Digit, option.ActionToolName, step.Id);
            return ValueTask.FromResult<DtmfActionResult>(
                new DtmfActionResult.Reject(option.OnFailurePrompt, option.OnFailureAudioFile));
        }

        return InvokeActionAsync(
            tool,
            option.Arguments,
            extraArguments,
            option.NextStepId,
            option.OnFailurePrompt,
            option.OnFailureAudioFile,
            cancellationToken);
    }

    public async ValueTask<DtmfActionResult> InvokeActionAsync(
        AITool tool,
        IReadOnlyDictionary<string, object?>? boundArguments,
        IReadOnlyDictionary<string, object?>? extraArguments,
        string? successNextStepId,
        string? failurePrompt,
        Uri? failureAudio,
        CancellationToken cancellationToken)
    {
        if (tool is not AIFunction fn)
        {
            _logger.LogWarning(
                "DTMF action tool '{Name}' is not an AIFunction and cannot be invoked.",
                tool.Name);
            return new DtmfActionResult.Reject(failurePrompt, failureAudio);
        }

        if (CurrentStep is { Guards: { Count: > 0 } guards } gatedStep)
        {
            for (var i = 0; i < guards.Count; i++)
            {
                var guard = guards[i];
                var gr = await guard.EvaluateAsync(State, cancellationToken).ConfigureAwait(false);
                if (!gr.Passed)
                {
                    _logger.LogInformation(
                        "DTMF action '{Tool}' blocked by guard '{GuardType}' on step '{Step}': {Reason}",
                        fn.Name, guard.GetType().Name, gatedStep.Id, gr.FailureReason);
                    return new DtmfActionResult.Reject(failurePrompt, failureAudio);
                }
            }
        }

        var args = new AIFunctionArguments { Services = _services };
        if (boundArguments is not null)
        {
            foreach (var kv in boundArguments)
            {
                args[kv.Key] = kv.Value;
            }
        }
        if (extraArguments is not null)
        {
            foreach (var kv in extraArguments)
            {
                args[kv.Key] = kv.Value;
            }
        }

        object? raw;
        try
        {
            raw = await fn.InvokeAsync(args, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DTMF action tool '{Name}' threw; treating as rejection.", tool.Name);
            return new DtmfActionResult.Reject(failurePrompt, failureAudio);
        }

        return InterpretResult(raw, successNextStepId, failurePrompt, failureAudio);
    }

    public string BuildCurrentStepPrompt(ConversationContext? context = null) =>
        CurrentStep is { } step
            ? ActiveDefinition.BuildPromptForStep(step, State, context)
            : RealtimeAIPromptTemplate.Render(ActiveDefinition.BasePrompt);

    public IEnumerable<AITool> WrapToolsWithCurrentGuards(IEnumerable<AITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        if (CurrentStep is not { Guards: { Count: > 0 } guards })
        {
            return tools;
        }

        return GuardedAIFunction.WrapTools(tools, guards, () => State);
    }

    public string BuildDtmfMenuPrompt(RealtimeIvrWorkflowStep step)
    {
        var sb = new StringBuilder();
        var intro = step.ConversationState.Description ?? step.ConversationState.Goal;
        if (!string.IsNullOrWhiteSpace(intro))
        {
            sb.AppendLine(intro);
        }
        if (step.StepScriptedConfiguration?.Dtmf?.MenuOptions is { Count: > 0 } menu)
        {
            foreach (var (digit, option) in menu)
            {
                sb.AppendLine($"Press {digit} for {option.Label}.");
            }
        }
        return sb.ToString();
    }

    private static DtmfActionResult InterpretResult(
        object? raw,
        string? successNextStepId,
        string? failurePrompt,
        Uri? failureAudio)
    {
        switch (raw)
        {
            case null:
                return successNextStepId is not null
                    ? new DtmfActionResult.Transition(successNextStepId)
                    : new DtmfActionResult.Repeat();
            case DtmfActionResult typed:
                return typed;
        }

        // Reflection fallback for envelopes like CallControlResult { bool Success; string Message; }.
        var type = raw.GetType();
        var successProp = type.GetProperty("Success") ?? type.GetProperty("IsSuccess");
        if (successProp is not null && successProp.PropertyType == typeof(bool))
        {
            var success = (bool)(successProp.GetValue(raw) ?? false);
            if (!success)
            {
                return new DtmfActionResult.Reject(failurePrompt, failureAudio);
            }
        }

        return successNextStepId is not null
            ? new DtmfActionResult.Transition(successNextStepId)
            : new DtmfActionResult.Repeat();
    }
}

/// <summary>
/// Adapter <see cref="IIvrWorkflowCatalog"/> used by the single-definition navigator
/// constructor. Exposes the bound workflow under its own name; every other id is
/// "unknown" so subflow pushes against this catalog fail loudly with a clear message.
/// </summary>
internal sealed class SingleDefinitionCatalog : IIvrWorkflowCatalog
{
    private readonly RealtimeIvrWorkflowDefinition _definition;
    private readonly Compilation.CompiledIvrWorkflow _compiled;

    public SingleDefinitionCatalog(RealtimeIvrWorkflowDefinition definition)
    {
        _definition = definition;
        _compiled = new Compilation.CompiledIvrWorkflow
        {
            Name = definition.Name,
            Runtime = definition,
            Strategy = Strategies.IvrStrategyPolicy.Default,
            Stages = [],
            Capabilities = new Dictionary<string, Compilation.CompiledIvrCapability>(StringComparer.Ordinal),
            IntentExamples = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
        };
    }

    public IReadOnlyCollection<string> Ids => [_definition.Name];

    public IReadOnlyCollection<int> VersionsFor(string workflowId)
        => string.Equals(workflowId, _definition.Name, StringComparison.Ordinal)
            ? [_compiled.Version >= 1 ? _compiled.Version : 1]
            : [];

    public bool TryGet(string workflowId, [NotNullWhen(true)] out Compilation.CompiledIvrWorkflow? workflow)
        => TryGet(workflowId, minVersion: null, maxVersion: null, out workflow);

    public bool TryGet(
        string workflowId,
        int? minVersion,
        int? maxVersion,
        [NotNullWhen(true)] out Compilation.CompiledIvrWorkflow? workflow)
    {
        if (string.Equals(workflowId, _definition.Name, StringComparison.Ordinal))
        {
            var v = _compiled.Version >= 1 ? _compiled.Version : 1;
            if ((minVersion is null || v >= minVersion) && (maxVersion is null || v <= maxVersion))
            {
                workflow = _compiled;
                return true;
            }
        }
        workflow = null;
        return false;
    }

    public Compilation.CompiledIvrWorkflow Get(string workflowId) => Get(workflowId, null, null);

    public Compilation.CompiledIvrWorkflow Get(string workflowId, int? minVersion, int? maxVersion)
    {
        if (!TryGet(workflowId, minVersion, maxVersion, out var compiled))
        {
            throw new KeyNotFoundException(
                $"This navigator was built with the single workflow '{_definition.Name}'; cannot resolve '{workflowId}'. " +
                "Construct the navigator with an IIvrWorkflowCatalog to enable subflow resolution.");
        }
        return compiled;
    }

    public ValueTask EnsureLoadedAsync(CancellationToken cancellationToken = default) => default;
}
