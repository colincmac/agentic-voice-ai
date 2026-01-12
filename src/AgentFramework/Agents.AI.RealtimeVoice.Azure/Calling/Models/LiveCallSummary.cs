namespace Agents.AI.RealtimeVoice.Azure.Calling.Models;

/// <summary>
/// Status of a live call from an operator's perspective.
/// </summary>
public enum LiveCallStatus
{
    /// <summary>Call is being set up.</summary>
    Connecting,

    /// <summary>Call is actively ongoing.</summary>
    Active,

    /// <summary>Call is on hold.</summary>
    OnHold,

    /// <summary>Call has ended normally.</summary>
    Ended,

    /// <summary>Call failed or was dropped.</summary>
    Failed
}

/// <summary>
/// Represents a summary of a live call for operator dashboard monitoring.
/// </summary>
public sealed class LiveCallSummary
{
    /// <summary>
    /// Unique identifier for the conversation session.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// ACS call connection identifier, if applicable.
    /// </summary>
    public string? CallConnectionId { get; set; }

    /// <summary>
    /// Timestamp when the call started.
    /// </summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// Timestamp when the call ended, if it has ended.
    /// </summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>
    /// Current status of the call.
    /// </summary>
    public LiveCallStatus Status { get; set; } = LiveCallStatus.Active;

    /// <summary>
    /// List of participants in the call.
    /// </summary>
    public IList<LiveParticipantSummary> Participants { get; init; } = [];

    // ─────────────────────────────────────────────────────────────────────────────
    // Health Metrics (Phase 3)
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Customer sentiment score from -1 (very negative) to 1 (very positive).
    /// Null if not yet computed.
    /// </summary>
    public double? CustomerSentiment { get; set; }

    /// <summary>
    /// Agent sentiment score from -1 (very negative) to 1 (very positive).
    /// Null if not yet computed.
    /// </summary>
    public double? AgentSentiment { get; set; }

    /// <summary>
    /// Task adherence score from 0 (not following script/tasks) to 1 (fully adherent).
    /// Null if not yet computed.
    /// </summary>
    public double? TaskAdherenceScore { get; set; }

    /// <summary>
    /// Escalation risk score from 0 (low risk) to 1 (high risk).
    /// Null if not yet computed.
    /// </summary>
    public double? EscalationRiskScore { get; set; }

    /// <summary>
    /// List of currently active tasks or intents being handled.
    /// </summary>
    public IReadOnlyList<string> ActiveTasks { get; set; } = [];

    /// <summary>
    /// Brief summary of the most recent utterance for quick operator review.
    /// </summary>
    public string? LatestUtteranceSummary { get; set; }

    // ─────────────────────────────────────────────────────────────────────────────
    // Computed Properties
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Current duration of the call.
    /// </summary>
    public TimeSpan Duration => (EndedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    /// <summary>
    /// Creates a deep copy of this summary.
    /// </summary>
    public LiveCallSummary Clone()
    {
        return new LiveCallSummary
        {
            SessionId = SessionId,
            CallConnectionId = CallConnectionId,
            StartedAt = StartedAt,
            EndedAt = EndedAt,
            Status = Status,
            Participants = Participants.Select(p => p.Clone()).ToList(),
            CustomerSentiment = CustomerSentiment,
            AgentSentiment = AgentSentiment,
            TaskAdherenceScore = TaskAdherenceScore,
            EscalationRiskScore = EscalationRiskScore,
            ActiveTasks = ActiveTasks.ToList(),
            LatestUtteranceSummary = LatestUtteranceSummary
        };
    }
}
