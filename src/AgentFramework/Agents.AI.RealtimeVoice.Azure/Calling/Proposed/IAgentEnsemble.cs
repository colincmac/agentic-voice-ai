using System.Threading.Channels;
using Microsoft.Agents.AI;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed;

// SKETCH — sub-contract used by AgentEnsembleStrategy.
//
// Captures the "primary speaker + parallel delegates" shape explicitly:
//   - exactly one PrimaryAgent at any moment is the voice the caller hears
//   - DelegateAgents run in parallel doing tool calls / lookups / risk checks
//   - delegates push AgentInsight back to the primary's working context
//   - PromoteAsync hands off the speaker role (specialist takeover, supervisor barge-in)
//
// This shape is private to AgentEnsembleStrategy. ICallSession does not see the
// individual agents — it sees a single IConversationStrategy and the AgentSpeakingChanged
// events on the StrategyEvent stream.

/// <summary>
/// Orchestration primitive for an AI brain composed of multiple cooperating agents.
/// </summary>
public interface IAgentEnsemble : IAsyncDisposable
{
    /// <summary>The agent currently speaking to the caller.</summary>
    IConversationalAgent PrimaryAgent { get; }

    /// <summary>Background agents producing insights, performing tool work, watching context.</summary>
    IReadOnlyList<IDelegateAgent> DelegateAgents { get; }

    /// <summary>
    /// Hand off the active-speaker role. The previous primary stops generating audio
    /// and is added to the delegate pool (or removed entirely if <paramref name="removePrevious"/>).
    /// </summary>
    ValueTask PromoteAsync(string delegateAgentId, bool removePrevious = false, CancellationToken cancellationToken = default);

    /// <summary>Insights produced by delegate agents; consumed by the primary's prompt context.</summary>
    ChannelReader<AgentInsight> Insights { get; }

    /// <summary>Add a delegate at runtime (e.g., escalation specialist joins to monitor).</summary>
    ValueTask AddDelegateAsync(IDelegateAgent agent, CancellationToken cancellationToken = default);

    ValueTask RemoveDelegateAsync(string agentId, CancellationToken cancellationToken = default);
}

/// <summary>
/// An agent that can be the active speaker on a call: produces audio (or text + TTS)
/// and consumes caller utterances.
/// </summary>
public interface IConversationalAgent
{
    string AgentId { get; }
    string DisplayName { get; }
    AIAgent Agent { get; }
    AgentSession Session { get; }
}

/// <summary>
/// A background agent that does not speak directly to the caller. Receives the
/// rolling transcript + caller signals, emits <see cref="AgentInsight"/>s.
/// </summary>
public interface IDelegateAgent
{
    string AgentId { get; }
    string DisplayName { get; }
    DelegateAgentRole Role { get; }
    AIAgent Agent { get; }

    /// <summary>
    /// Called by the ensemble whenever new context is available.
    /// Implementations decide whether to act, push results to <c>insights</c>.
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
