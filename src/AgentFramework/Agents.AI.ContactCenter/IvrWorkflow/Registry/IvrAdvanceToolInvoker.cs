using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.IvrWorkflow.Registry;

/// <summary>
/// Executes the IVR <c>advance</c> tool's resolve → transition → re-arm pipeline inside
/// the realtime agent's function-invocation loop, returning a structured
/// <see cref="AdvanceToolResult"/> the model can observe.
/// </summary>
/// <remarks>
/// <para>
/// The synthesized <see cref="IvrAdvanceTool"/> used to be a pure echo function whose
/// returned text bore no relationship to whether the navigator actually advanced — the
/// strategy mutated the navigator out-of-band by listening for
/// <see cref="Calling.RealtimeBackendUpdate.FunctionCalled"/>. That left the realtime
/// model unaware of unknown choices, intents without a transition, and navigator
/// rejections; it would happily speak as if the workflow had moved on. This invoker
/// runs the same logic inline so the model receives a meaningful
/// <see cref="AdvanceToolResult"/> and can self-correct or verbalize the failure.
/// </para>
/// <para>
/// Requires the realtime conversation client pipeline to include
/// <c>UseFunctionInvocation()</c>; otherwise the tool body never runs.
/// </para>
/// <para>
/// Reads <see cref="IIvrWorkflowNavigator.CurrentStep"/> lazily on every invocation so a
/// single instance can be shared across stage transitions.
/// </para>
/// </remarks>
public sealed class IvrAdvanceToolInvoker
{
    private readonly IIvrWorkflowNavigator _navigator;
    private readonly Func<RealtimeIvrWorkflowStep, CancellationToken, Task> _applyStageAsync;
    private readonly ILogger _logger;

    /// <param name="navigator">The per-call navigator that owns the workflow state machine.</param>
    /// <param name="applyStageAsync">
    /// Strategy callback invoked after a successful navigator transition to push the new
    /// step's prompt + tool surface onto the realtime backend. Must serialize with any
    /// other navigator mutators (the strategy's <c>_navigatorLock</c>).
    /// </param>
    /// <param name="logger">Optional logger; defaults to <see cref="NullLogger"/>.</param>
    public IvrAdvanceToolInvoker(
        IIvrWorkflowNavigator navigator,
        Func<RealtimeIvrWorkflowStep, CancellationToken, Task> applyStageAsync,
        ILogger<IvrAdvanceToolInvoker>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        ArgumentNullException.ThrowIfNull(applyStageAsync);

        _navigator = navigator;
        _applyStageAsync = applyStageAsync;
        _logger = logger ?? NullLogger<IvrAdvanceToolInvoker>.Instance;
    }

    /// <summary>
    /// Resolve <paramref name="nextStage"/> against the current step, transition the
    /// navigator, and re-arm the realtime backend with the new step's prompt + tools.
    /// Every outcome is surfaced as a typed <see cref="AdvanceToolResult"/>.
    /// </summary>
    public async Task<AdvanceToolResult> InvokeAsync(string nextStage, CancellationToken cancellationToken)
    {
        var currentStep = _navigator.CurrentStep;
        if (currentStep is null)
        {
            _logger.LogWarning("Advance tool invoked with '{Chosen}' but no current step is set.", nextStage);
            return new AdvanceToolResult(
                AdvanceToolResult.StatusNoCurrentStep,
                "Cannot advance: the workflow has no current step.");
        }

        if (string.IsNullOrWhiteSpace(nextStage))
        {
            var allowed = IvrAdvanceTool.CollectAdvanceTargets(currentStep);
            return new AdvanceToolResult(
                AdvanceToolResult.StatusUnknownChoice,
                $"Cannot advance: 'next_stage' is required. Allowed values: {string.Join(", ", allowed)}.",
                From: currentStep.Id,
                Reason: "next_stage was null or empty.",
                AllowedTargets: allowed);
        }

        var resolution = IvrAdvanceTool.Resolve(currentStep, nextStage);

        switch (resolution.Kind)
        {
            case AdvanceResolutionKind.Unknown:
                {
                    var allowed = IvrAdvanceTool.CollectAdvanceTargets(currentStep);
                    _logger.LogInformation(
                        "Advance choice '{Chosen}' on step '{Step}' is not a valid intent or transition target.",
                        nextStage, currentStep.Id);
                    return new AdvanceToolResult(
                        AdvanceToolResult.StatusUnknownChoice,
                        $"'{nextStage}' is not a valid next stage on step '{currentStep.Id}'. Allowed values: {string.Join(", ", allowed)}.",
                        From: currentStep.Id,
                        Reason: $"'{nextStage}' is not among the step's intents or valid transitions.",
                        AllowedTargets: allowed);
                }

            case AdvanceResolutionKind.IntentWithoutTransition:
                {
                    var intentName = resolution.ResolvedIntent?.Name ?? nextStage;
                    _logger.LogInformation(
                        "Advance resolved intent '{Intent}' on step '{Step}' has no NextStepId; not transitioning.",
                        intentName, currentStep.Id);
                    return new AdvanceToolResult(
                        AdvanceToolResult.StatusIntentWithoutTransition,
                        $"Intent '{intentName}' is recognized but does not transition to another stage. Invoke its capability tool instead.",
                        From: currentStep.Id,
                        Reason: "Intent has no NextStepId.");
                }

            case AdvanceResolutionKind.Stage:
            case AdvanceResolutionKind.Intent:
                {
                    var target = resolution.TargetStageId!;

                    // Phase 3: consult per-transition + per-stage guards before applying.
                    // The navigator returns a verdict that may require an auth-resolver
                    // detour via PushSubflowAsync before the actual transition fires.
                    var evaluation = await _navigator.EvaluateTransitionAsync(target, cancellationToken).ConfigureAwait(false);

                    switch (evaluation)
                    {
                        case TransitionEvaluation.Allowed allowed:
                        {
                            var tr = _navigator.TransitionTo(target);
                            if (!tr.Succeeded || tr.NewStep is null)
                            {
                                _logger.LogWarning(
                                    "Advance to '{Target}' from '{Current}' rejected by navigator after Allowed evaluation: {Reason}",
                                    target, currentStep.Id, tr.Reason);
                                return new AdvanceToolResult(
                                    AdvanceToolResult.StatusTransitionRejected,
                                    $"Cannot advance to '{target}': {tr.Reason ?? "navigator rejected the transition."}",
                                    From: currentStep.Id,
                                    To: target,
                                    Reason: tr.Reason);
                            }

                            await _applyStageAsync(tr.NewStep, cancellationToken).ConfigureAwait(false);

                            var status = tr.NewStep.Terminal
                                ? AdvanceToolResult.StatusAdvancedTerminal
                                : AdvanceToolResult.StatusAdvanced;
                            var message = tr.NewStep.Terminal
                                ? $"Advanced to terminal stage '{tr.NewStep.Id}'. The workflow is complete."
                                : $"Advanced to stage '{tr.NewStep.Id}'.";

                            return new AdvanceToolResult(
                                status, message,
                                From: currentStep.Id,
                                To: tr.NewStep.Id,
                                Terminal: tr.NewStep.Terminal);
                        }

                        case TransitionEvaluation.RequiresDetour detour:
                        {
                            // Cache the original intent so subflow prompts can surface it
                            // (Collected Information renders all state keys automatically),
                            // then push the resolver subflow with the original target as
                            // the return step. PopFrameAsync will chain another detour if
                            // a stricter guard still fails post-resume.
                            _navigator.State.Set(
                                PendingIntent.StateKey,
                                new PendingIntent(target, _navigator.Definition.Name, resolution.ResolvedIntent?.Name));

                            _logger.LogInformation(
                                "Advance to '{Target}' detouring through '{Resolver}' ({Subflow}) to satisfy '{Guard}'.",
                                target, detour.ResolverDescription, detour.ResolverWorkflowId, detour.UnmetGuard.GetType().Name);

                            var childInitial = await _navigator.PushSubflowAsync(
                                detour.ResolverWorkflowId,
                                returnToStepId: target,
                                failureReturnStepId: detour.Target.OnUnauthorizedStepId
                                    ?? _navigator.Definition.UnauthorizedFailureStepId,
                                detour.MinVersion,
                                detour.MaxVersion,
                                cancellationToken).ConfigureAwait(false);

                            await _applyStageAsync(childInitial, cancellationToken).ConfigureAwait(false);

                            return new AdvanceToolResult(
                                AdvanceToolResult.StatusAdvanced,
                                $"Routing through '{detour.ResolverWorkflowId}' to satisfy {detour.ResolverDescription} before continuing to '{target}'.",
                                From: currentStep.Id,
                                To: childInitial.Id);
                        }

                        case TransitionEvaluation.BlockedNoResolver blocked:
                            _logger.LogWarning(
                                "Advance to '{Target}' blocked: {Reason} (no resolver registered for guard '{Guard}')",
                                target, blocked.Reason, blocked.UnmetGuard.GetType().Name);
                            return new AdvanceToolResult(
                                AdvanceToolResult.StatusTransitionRejected,
                                $"Cannot advance to '{target}': {blocked.Reason}",
                                From: currentStep.Id,
                                To: target,
                                Reason: blocked.Reason);

                        case TransitionEvaluation.Invalid invalid:
                            _logger.LogWarning(
                                "Advance to '{Target}' from '{Current}' rejected by navigator: {Reason}",
                                target, currentStep.Id, invalid.Reason);
                            return new AdvanceToolResult(
                                AdvanceToolResult.StatusTransitionRejected,
                                $"Cannot advance to '{target}': {invalid.Reason}",
                                From: currentStep.Id,
                                To: target,
                                Reason: invalid.Reason);
                    }

                    // Should be exhaustive — defensive fallback.
                    return new AdvanceToolResult(
                        AdvanceToolResult.StatusTransitionRejected,
                        $"Unrecognized transition evaluation for '{target}'.",
                        From: currentStep.Id,
                        To: target);
                }

            default:
                return new AdvanceToolResult(
                    AdvanceToolResult.StatusUnknownChoice,
                    $"'{nextStage}' could not be resolved.",
                    From: currentStep.Id,
                    Reason: $"Unhandled resolution kind '{resolution.Kind}'.");
        }
    }
}
