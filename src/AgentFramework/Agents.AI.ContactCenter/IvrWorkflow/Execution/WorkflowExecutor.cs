using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Navigation;

// Fully qualify nested-record references to avoid colliding with the legacy
// IvrWorkflow.TransitionEvaluation that's still in scope via the parent namespace.
using NewEval = Agents.AI.ContactCenter.IvrWorkflow.Navigation.TransitionEvaluation;

namespace Agents.AI.ContactCenter.IvrWorkflow.Execution;

/// <summary>Result returned from <see cref="WorkflowExecutor.AdvanceToAsync"/>.</summary>
public abstract record AdvanceOutcome
{
    /// <summary>Transition succeeded; the workflow is now on <see cref="NewStage"/>.</summary>
    public sealed record Advanced(CompiledStage NewStage) : AdvanceOutcome;

    /// <summary>
    /// Transition was blocked by a predicate but the edge declared an <c>onBlocked</c>
    /// fallback. The workflow is now on <see cref="NewStage"/> (the fallback target) and
    /// <see cref="Reason"/> describes the original denial.
    /// </summary>
    public sealed record AdvancedToFallback(CompiledStage NewStage, string Reason) : AdvanceOutcome;

    /// <summary>Transition was denied and no fallback exists. The current stage is unchanged.</summary>
    public sealed record Denied(string Reason) : AdvanceOutcome;

    /// <summary>The requested target stage does not exist on the current stage's outgoing edges.</summary>
    public sealed record Invalid(string Reason) : AdvanceOutcome;
}

/// <summary>
/// Single-advance API consumed by every per-tier strategy. Delegates routing to the
/// navigator; on a successful transition invokes a tier-supplied render callback so the
/// strategy can push the new stage's prompt + tools onto its underlying transport.
/// </summary>
/// <remarks>
/// Replaces the legacy <c>IvrAdvanceFunctions</c> + per-strategy <c>ApplyStepAsync</c>
/// recursion. The executor is thread-safe: stage transitions are serialized by an
/// internal lock so the realtime tier (where advances may be triggered concurrently by
/// the model and inbound DTMF) keeps the navigator + backend in sync.
/// </remarks>
public sealed class WorkflowExecutor
{
    private readonly CallWorkflowSession _session;
    private readonly Func<CompiledStage, CancellationToken, ValueTask> _renderAsync;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkflowExecutor(
        CallWorkflowSession session,
        Func<CompiledStage, CancellationToken, ValueTask> renderAsync)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(renderAsync);

        _session = session;
        _renderAsync = renderAsync;
    }

    public CallWorkflowSession Session => _session;

    public ICallWorkflowNavigator Navigator => _session.Navigator;

    /// <summary>
    /// Enter the workflow's initial stage (or resume from prior state) and render it.
    /// </summary>
    public async ValueTask<CompiledStage> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var stage = _session.Navigator.EnterInitialStage();
            await _renderAsync(stage, cancellationToken).ConfigureAwait(false);
            return stage;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Attempt to advance to <paramref name="targetStageId"/>. The navigator evaluates the
    /// edge predicate; on Allowed the edge is applied and the render callback fires; on
    /// BlockedRoutedTo the fallback edge is applied and the callback fires; on
    /// Blocked / Invalid no transition occurs.
    /// </summary>
    public async ValueTask<AdvanceOutcome> AdvanceToAsync(
        string targetStageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetStageId);

        // Stage transitions are atomic — acquire the lock uncancelled so a partially-
        // applied transition can't leave the navigator and transport out of sync.
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var evaluation = await _session.Navigator
                .EvaluateTransitionAsync(targetStageId, cancellationToken)
                .ConfigureAwait(false);

            switch (evaluation)
            {
                case NewEval.Allowed allowed:
                {
                    var newStage = _session.Navigator.ApplyTransition(allowed.Edge);
                    await _renderAsync(newStage, cancellationToken).ConfigureAwait(false);
                    return new AdvanceOutcome.Advanced(newStage);
                }

                case NewEval.BlockedRoutedTo routed:
                {
                    var newStage = _session.Navigator.ApplyTransition(routed.FallbackEdge);
                    await _renderAsync(newStage, cancellationToken).ConfigureAwait(false);
                    return new AdvanceOutcome.AdvancedToFallback(newStage, routed.Reason);
                }

                case NewEval.Blocked blocked:
                    return new AdvanceOutcome.Denied(blocked.Reason);

                case NewEval.Invalid invalid:
                    return new AdvanceOutcome.Invalid(invalid.Reason);

                default:
                    throw new InvalidOperationException(
                        $"Unhandled transition evaluation: {evaluation.GetType().Name}.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Render the current stage. Useful after a tier swap restoration.</summary>
    public async ValueTask RenderCurrentAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_session.Navigator.CurrentStage is { } current)
            {
                await _renderAsync(current, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
