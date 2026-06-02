namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// One frame on the per-call <see cref="IvrWorkflowState.Frames"/> stack. Represents an
/// active workflow invocation: which workflow definition is executing, which step it is
/// on, when the step started, and (Phase 1+) where to return when the workflow exits.
/// </summary>
/// <remarks>
/// Only a single frame is ever pushed
/// (by the navigator on entry), and the existing <see cref="IvrWorkflowState.CurrentStepName"/>
/// / <see cref="IvrWorkflowState.CurrentStepIndex"/> / <see cref="IvrWorkflowState.StepStartedAt"/>
/// properties read and write through the top frame so every existing caller keeps working
/// unchanged. The <see cref="ReturnToStepId"/> and <see cref="FailureReturnStepId"/> hooks
/// are placeholders consumed by the subflow push/pop machinery; they are unused
/// today but reserve the in-memory shape now so we don't have to bump it later.
/// </remarks>
public sealed class WorkflowFrame
{
    /// <summary>Workflow identifier this frame is executing. Matches <c>RealtimeIvrWorkflowDefinition.Name</c>.</summary>
    /// <remarks>
    /// May be the empty string for frames created implicitly by the back-compat setters on
    /// <see cref="IvrWorkflowState"/> (callers that wrote <c>CurrentStepName</c> directly
    /// before going through the navigator). Phase 1 strategies should always push frames
    /// with a fully-populated identifier.
    /// </remarks>
    public required string WorkflowId { get; init; }

    /// <summary>
    /// Content version of the workflow this frame is executing. Phase 2 will start sourcing
    /// this from <c>CompiledIvrWorkflow.Version</c> / the YAML <c>version.content</c> field;
    /// Defaults to <c>1</c> for parity with the existing single-version model.
    /// </summary>
    public int WorkflowVersion { get; init; } = 1;

    /// <summary>The step id within <see cref="WorkflowId"/> the call is currently on.</summary>
    public required string CurrentStepId { get; set; }

    /// <summary>Index of <see cref="CurrentStepId"/> in the workflow's ordered step list, or <c>-1</c> when unknown.</summary>
    public int CurrentStepIndex { get; set; } = -1;

    /// <summary>When <see cref="CurrentStepId"/> was entered. Reset by the navigator on every transition.</summary>
    public DateTimeOffset? StepStartedAt { get; set; }

    /// <summary>
    /// Reserved for Phase 1: when a subflow is pushed, this records the step id in the
    /// parent frame to resume after the subflow completes successfully. Unused in Phase 0.
    /// </summary>
    public string? ReturnToStepId { get; init; }

    /// <summary>
    /// Reserved for Phase 1: parent-frame step id to enter when the subflow exits via a
    /// failure / cancellation terminal stage. Unused in Phase 0.
    /// </summary>
    public string? FailureReturnStepId { get; init; }
}
