using Agents.AI.ContactCenter.IvrWorkflow.Compilation;

namespace Agents.AI.ContactCenter.IvrWorkflow.Navigation;

/// <summary>
/// Result of <see cref="ICallWorkflowNavigator.EvaluateTransitionAsync"/>. Describes whether
/// a requested transition is permitted, blocked with a fallback target, or invalid.
/// </summary>
public abstract record TransitionEvaluation
{
    /// <summary>The transition is allowed; the navigator can call <see cref="ICallWorkflowNavigator.ApplyTransitionAsync"/>.</summary>
    public sealed record Allowed(CompiledStageEdge Edge) : TransitionEvaluation;

    /// <summary>
    /// The transition's predicate denied, but the edge declares an <c>onBlocked</c> fallback.
    /// Callers should route to <see cref="FallbackEdge"/> instead.
    /// </summary>
    public sealed record BlockedRoutedTo(CompiledStageEdge Edge, CompiledStageEdge FallbackEdge, string Reason)
        : TransitionEvaluation;

    /// <summary>
    /// The transition's predicate denied and the edge does not declare an <c>onBlocked</c>
    /// fallback. The caller should surface <see cref="Reason"/> back to the model.
    /// </summary>
    public sealed record Blocked(CompiledStageEdge Edge, string Reason) : TransitionEvaluation;

    /// <summary>No matching edge exists from the current stage to the requested target.</summary>
    public sealed record Invalid(string Reason) : TransitionEvaluation;
}
