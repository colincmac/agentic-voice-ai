namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// Phase 3: snapshot of the transition the workflow was attempting when an
/// auth-resolver detour was pushed. Written into <see cref="IvrWorkflowState"/> under
/// the well-known key <see cref="StateKey"/> so sub-workflow prompts (which render all
/// state keys under "Collected Information") can surface what the caller asked for.
/// </summary>
public sealed record PendingIntent(
    string TargetStepId,
    string ParentWorkflowId,
    string? Label = null)
{
    /// <summary>State key used by the strategy / navigator. Subflow prompt context picks it up automatically.</summary>
    public const string StateKey = "PendingIntent";
}
