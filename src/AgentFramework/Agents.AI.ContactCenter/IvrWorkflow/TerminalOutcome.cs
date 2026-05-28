namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// Outcome a terminal stage signals to a parent <see cref="WorkflowFrame"/> when a
/// sub-workflow pops. Phase 1's <see cref="IIvrWorkflowNavigator.PopFrameAsync(bool, System.Threading.CancellationToken)"/>
/// reads this off the popped frame's last step and routes the parent to
/// <see cref="WorkflowFrame.ReturnToStepId"/> (Success) or
/// <see cref="WorkflowFrame.FailureReturnStepId"/> (Failure).
/// </summary>
public enum TerminalOutcome
{
    /// <summary>Normal completion — parent resumes at its <c>onSuccess</c> step.</summary>
    Success,

    /// <summary>Failure / cancellation — parent resumes at its <c>onFailure</c> step.</summary>
    Failure,
}
