using System.Threading.Channels;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed;

// SKETCH — sub-contract used by AgentEnsembleStrategy.
//
// Captures the "primary speaker + parallel delegates" shape explicitly:
//   - SpeakerCandidates  — agents that own an IRealtimeVoiceBackend and CAN be the
//                          voice the caller hears. Exactly one is the active primary.
//   - Delegates          — text-only background workers (researcher, sentiment analyst,
//                          guardrail, scribe). They do not produce caller-facing audio;
//                          they consume conversation context and push AgentInsight back.
//   - PromoteAsync       — swap which speaker candidate is the active primary
//                          (specialist takeover, supervisor handoff to AI, etc.).
//   - PrimaryChanged     — fires after a swap so AgentEnsembleStrategy can re-pump
//                          the new primary's backend without missing a beat.
//
// This shape is consumed by AgentEnsembleStrategy. ICallSession does not see the
// individual agents — it sees a single IConversationStrategy and an
// AgentSpeakingChanged event on the StrategyEvent stream.

/// <summary>
/// Orchestration primitive for an AI brain composed of multiple cooperating agents.
/// </summary>
public interface IAgentEnsemble : IAsyncDisposable
{
    /// <summary>The candidate currently speaking to the caller.</summary>
    IConversationalAgent PrimaryAgent { get; }

    /// <summary>All agents capable of being the active speaker (backed by a realtime backend).</summary>
    IReadOnlyList<IConversationalAgent> SpeakerCandidates { get; }

    /// <summary>Background agents that never speak to the caller directly.</summary>
    IReadOnlyList<IDelegateAgent> Delegates { get; }

    /// <summary>
    /// Hand off the active-speaker role to a different speaker candidate.
    /// </summary>
    ValueTask PromoteAsync(string speakerCandidateId, CancellationToken cancellationToken = default);

    /// <summary>Insights produced by delegate agents; consumed by the strategy and surfaced as StrategyEvents.</summary>
    ChannelReader<AgentInsight> Insights { get; }

    /// <summary>Add a delegate at runtime (e.g., escalation specialist starts monitoring).</summary>
    ValueTask AddDelegateAsync(IDelegateAgent agent, CancellationToken cancellationToken = default);

    ValueTask RemoveDelegateAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised after <see cref="PromoteAsync"/> changes the primary. The strategy listens
    /// to this and switches its inbound-audio + outbound-update pumps to the new backend.
    /// </summary>
    event Func<IConversationalAgent, ValueTask>? PrimaryChanged;
}

/// <summary>
/// An agent that can be the active speaker on a call: owns an
/// <see cref="IRealtimeVoiceBackend"/> that produces audio + transcripts.
/// </summary>
public interface IConversationalAgent
{
    string AgentId { get; }
    string DisplayName { get; }
    IRealtimeVoiceBackend Backend { get; }
}

/// <summary>
/// A background agent that does not speak directly to the caller. Receives the
/// rolling transcript + caller signals via <see cref="OnContextAsync"/>, pushes
/// results to the supplied <see cref="ChannelWriter{T}"/> of <see cref="AgentInsight"/>.
/// </summary>
public interface IDelegateAgent
{
    string AgentId { get; }
    string DisplayName { get; }
    DelegateAgentRole Role { get; }

    /// <summary>
    /// Called by the ensemble whenever new context is available. Implementations
    /// decide whether to act and push results to <paramref name="insights"/>.
    /// </summary>
    ValueTask OnContextAsync(EnsembleContext context, ChannelWriter<AgentInsight> insights, CancellationToken cancellationToken = default);
}

public enum DelegateAgentRole
{
    /// <summary>Looks things up (CRM, KB, account).</summary>
    Researcher,

    /// <summary>Watches for compliance / safety violations.</summary>
    Guardrail,

    /// <summary>Tracks customer sentiment, frustration, intent stability.</summary>
    SentimentAnalyst,

    /// <summary>Pre-loads the next agent in case of escalation.</summary>
    StandbySpecialist,

    /// <summary>Generates structured wrap-up notes during the call.</summary>
    Scribe
}

public sealed record EnsembleContext(
    string CallId,
    IReadOnlyList<StrategyEvent.Transcript> RecentTranscripts,
    IReadOnlyList<StrategyEvent.AgentUtterance> RecentAgentUtterances,
    IReadOnlyList<AgentInsight> RecentInsights);

public sealed record AgentInsight(
    string AgentId,
    string Kind,                    // "lookup-result", "risk-flag", "sentiment", ...
    string Summary,
    object? Payload,
    double? Confidence,
    DateTimeOffset At);
