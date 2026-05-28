namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// Phase 3: a single declarative transition lowered from YAML, including any
/// per-transition <c>requires:</c> guards. Lives on
/// <see cref="RealtimeIvrWorkflowStep.TransitionRules"/> alongside the existing
/// <c>ConversationState.Transitions</c> (which strategies still consume for the
/// allowed-target enumeration / prompt rendering); the rule list adds the guard
/// metadata the navigator's auth-resolver detour needs.
/// </summary>
public sealed class TransitionRule
{
    /// <summary>Target step id. Matches <c>ConversationState.Transitions[].NextStep</c>.</summary>
    public required string TargetStepId { get; init; }

    /// <summary>
    /// Conversational trigger (free text, intent name, or condition string) authored on
    /// the YAML transition. Stored for diagnostics and round-trip tooling; the navigator
    /// matches on <see cref="TargetStepId"/>.
    /// </summary>
    public string? Condition { get; init; }

    /// <summary>
    /// Guards that must pass before this transition fires. Combined with the target
    /// step's stage-level <see cref="RealtimeIvrWorkflowStep.Guards"/> at evaluation
    /// time. Empty when the YAML transition declared no <c>requires:</c>.
    /// </summary>
    public IReadOnlyList<IIvrStepGuard> Guards { get; init; } = [];
}
