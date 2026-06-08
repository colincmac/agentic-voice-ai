using Agents.AI.ContactCenter.Authentication;

namespace Agents.AI.ContactCenter.IvrWorkflow.Predicates;

/// <summary>
/// State snapshot available to an <see cref="EdgePredicate"/> when the workflow runtime
/// evaluates a transition. Carries the per-call <see cref="IvrWorkflowState"/> and
/// <see cref="CallerAuthenticationState"/> plus the call's service scope so predicates can
/// resolve additional dependencies (e.g. an <see cref="ICallSessionAccessor"/>).
/// </summary>
/// <remarks>
/// Replaces the legacy <c>IvrStepGuard</c>/<c>IvrWorkflowGuards</c> contract. Predicates are
/// pure functions over the per-call state; they should not mutate it.
/// </remarks>
public sealed class WorkflowEdgeContext
{
    public WorkflowEdgeContext(
        IvrWorkflowState workflowState,
        CallerAuthenticationState? callerAuthentication)
    {
        ArgumentNullException.ThrowIfNull(workflowState);
        ArgumentNullException.ThrowIfNull(callerAuthentication);    
        WorkflowState = workflowState;
        CallerAuthentication = callerAuthentication;
    }

    /// <summary>Per-call IVR state (collected data, transcript, current stage id).</summary>
    public IvrWorkflowState WorkflowState { get; }

    /// <summary>The per-call caller-authentication state, or <see langword="null"/> when the host has no authenticator chain wired up.</summary>
    public CallerAuthenticationState? CallerAuthentication { get; }

}
