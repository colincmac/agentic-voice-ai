using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Predicates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.IvrWorkflow.Navigation;

/// <summary>
/// Default <see cref="ICallWorkflowNavigator"/>. Walks a <see cref="CompiledCallWorkflow"/>
/// by edge: every transition request looks up the matching outgoing edge from
/// <see cref="ICallWorkflowNavigator.CurrentStage"/>, evaluates its predicate against a
/// <see cref="WorkflowEdgeContext"/> assembled from per-call DI, and returns one of the
/// <see cref="TransitionEvaluation"/> variants.
/// </summary>
/// <remarks>
/// No subflow stack, no frame management — by design. Workflows in the new model are
/// self-contained; auth detours are explicit transitions with an <c>onBlocked</c> fallback.
/// State preserved across tier swaps lives in <see cref="IvrWorkflowState"/>; the navigator
/// reads <see cref="IvrWorkflowState.CurrentStepName"/> in <see cref="EnterInitialStage"/>
/// to resume.
/// </remarks>
public sealed class CallWorkflowNavigator : ICallWorkflowNavigator
{
    private readonly ILogger<CallWorkflowNavigator> _logger;
    private CompiledStage? _currentStage;

    public CallWorkflowNavigator(
        CompiledCallWorkflow workflow,
        IvrWorkflowState state,
        CallerAuthenticationState callerAuthenticationState,
        ILogger<CallWorkflowNavigator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(callerAuthenticationState);
        Workflow = workflow;
        State = state;
        CallerAuthenticationState = callerAuthenticationState;
        _logger = logger ?? NullLogger<CallWorkflowNavigator>.Instance;
    }

    public CompiledCallWorkflow Workflow { get; }

    public IvrWorkflowState State { get; }

    public CallerAuthenticationState CallerAuthenticationState { get; }

    public CompiledStage? CurrentStage => _currentStage;

    public bool IsComplete => _currentStage is { Terminal: true } || State.IsComplete;

    public CompiledStage EnterInitialStage()
    {
        // Tier swap restoration: if a prior tier already advanced the workflow, resume
        // there. Otherwise start at the blueprint's initial stage.
        var resumeId = State.CurrentStepName;
        if (!string.IsNullOrEmpty(resumeId)
            && Workflow.TryGetStage(resumeId, out var resumed))
        {
            _currentStage = resumed;
            if (State.Status is IvrWorkflowStatus.NotStarted)
            {
                State.Status = IvrWorkflowStatus.Running;
            }
            return resumed;
        }

        _currentStage = Workflow.InitialStage;
        State.CurrentStepName = _currentStage.Id;
        if (State.Status is IvrWorkflowStatus.NotStarted)
        {
            State.Status = IvrWorkflowStatus.Running;
        }
        return _currentStage;
    }

    public ValueTask<TransitionEvaluation> EvaluateTransitionAsync(
        string targetStageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetStageId);

        var current = _currentStage ?? throw new InvalidOperationException(
            "Navigator has no current stage. Call EnterInitialStage() first.");

        var edge = current.FindEdgeTo(targetStageId);
        if (edge is null)
        {
            return new ValueTask<TransitionEvaluation>(new TransitionEvaluation.Invalid(
                $"Stage '{current.Id}' has no outgoing transition to '{targetStageId}'."));
        }

        return EvaluateTransitionAsync(edge, cancellationToken);
    }

    public async ValueTask<TransitionEvaluation> EvaluateTransitionAsync(
        CompiledStageEdge edge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edge);

        var current = _currentStage ?? throw new InvalidOperationException(
            "Navigator has no current stage. Call EnterInitialStage() first.");

        var context = BuildEdgeContext();
        var result = await edge.Predicate(context, cancellationToken).ConfigureAwait(false);
        if (result.Passed)
        {
            return new TransitionEvaluation.Allowed(edge);
        }

        var reason = result.FailureReason ?? "Transition denied.";

        if (!string.IsNullOrEmpty(edge.OnBlockedStageId)
            && Workflow.TryGetStage(edge.OnBlockedStageId, out var fallbackStage))
        {
            // Synthesize a virtual fallback edge so callers can apply it via the same API.
            var fallbackEdge = current.FindEdgeTo(fallbackStage.Id)
                ?? new CompiledStageEdge(
                    new Blueprint.TransitionBlueprint
                    {
                        TargetStageId = fallbackStage.Id,
                        Label = $"onBlocked:{edge.TargetStageId}",
                    },
                    BuiltInPredicates.Always(),
                    onBlockedStageId: null);

            _logger.LogInformation(
                "Transition '{From}' → '{To}' blocked ({Reason}); routing to onBlocked '{Fallback}'.",
                current.Id, edge.TargetStageId, reason, fallbackStage.Id);

            return new TransitionEvaluation.BlockedRoutedTo(edge, fallbackEdge, reason);
        }

        _logger.LogInformation(
            "Transition '{From}' → '{To}' blocked ({Reason}); no fallback declared.",
            current.Id, edge.TargetStageId, reason);
        return new TransitionEvaluation.Blocked(edge, reason);
    }

    public CompiledStage ApplyTransition(CompiledStageEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);

        var target = Workflow.GetStage(edge.TargetStageId);

        if (_currentStage is { } current)
        {
            State.MarkStepCompleted(current.Id);
        }

        _currentStage = target;
        State.CurrentStepName = target.Id;

        if (target.Terminal)
        {
            State.Status = target.Blueprint.TerminalOutcome switch
            {
                Blueprint.BlueprintTerminalOutcome.Success => IvrWorkflowStatus.Completed,
                Blueprint.BlueprintTerminalOutcome.Failure => IvrWorkflowStatus.Failed,
                Blueprint.BlueprintTerminalOutcome.Abandoned => IvrWorkflowStatus.Cancelled,
                Blueprint.BlueprintTerminalOutcome.Escalated => IvrWorkflowStatus.Completed,
                _ => IvrWorkflowStatus.Completed,
            };
        }

        return target;
    }

    private WorkflowEdgeContext BuildEdgeContext()
    {
        return new WorkflowEdgeContext(State, CallerAuthenticationState);
    }
}
