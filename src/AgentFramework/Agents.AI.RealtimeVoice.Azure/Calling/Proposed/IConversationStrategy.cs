using System.Threading.Channels;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.RealtimeVoice.Azure.Configuration;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed;

// SKETCH — proposed replacement for the "AI brain" half of IChannelTransport.
//
// One IConversationStrategy per call. Implementations:
//   RealtimeVoiceStrategy        — wraps an AuthorizingRealtimeAIAgent
//   SttTtsStrategy               — STT → IChatClient → TTS
//   NluStrategy                  — STT → IIntentClassifier → workflow
//   DtmfStrategy                 — DTMF → workflow → TTS
//   AgentEnsembleStrategy        — primary speaker + parallel delegates (see IAgentEnsemble)
//   CompositeFallbackStrategy    — wraps an ordered list, swaps on failure (replaces FallbackOrchestrator)
//
// Strategies do not own a socket. They consume from caller channels (passed in via
// ICallSession when started) and write to OutboundAudio/Events channels that the
// session pumps to the caller edge.

/// <summary>
/// The "brain" of an IVR call: takes caller input, produces audio + workflow events.
/// </summary>
public interface IConversationStrategy : IAsyncDisposable
{
    StrategyKind Kind { get; }

    /// <summary>Tier this strategy implements, for capacity tracking and dashboards.</summary>
    AgentTier Tier { get; }

    /// <summary>Workflow state — shared across tier swaps for graceful degradation.</summary>
    IvrWorkflowState WorkflowState { get; }

    /// <summary>The set of <see cref="OutboundDirective"/> kinds this strategy emits.</summary>
    EdgeCapabilities EmittedDirectives { get; }

    /// <summary>
    /// Outbound directives the session pumps to the caller edge: audio frames
    /// (streaming edges), or speak/play/recognize verbs (verb-based edges).
    /// </summary>
    ChannelReader<OutboundDirective> Outbound { get; }

    /// <summary>
    /// Structured events emitted by the strategy: transcripts, agent insights,
    /// workflow transitions, intent classifications, function call requests, etc.
    /// Consumed by the session for context projection and by observers.
    /// </summary>
    ChannelReader<StrategyEvent> Events { get; }

    Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pause output (e.g., supervisor barge-in). Inbound is still delivered so the
    /// strategy stays caught up; the session simply stops pumping OutboundAudio.
    /// </summary>
    ValueTask SuspendAsync(CancellationToken cancellationToken = default);

    ValueTask ResumeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Everything a strategy needs from the call container at start.
/// </summary>
public sealed record StrategyStartContext
{
    public required string CallId { get; init; }

    /// <summary>Caller audio fanned in from the call edge.</summary>
    public required ChannelReader<AudioFrame> InboundAudio { get; init; }

    public required ChannelReader<DtmfTone> InboundDtmf { get; init; }

    /// <summary>Call-scoped DI for resolving agents, recognizers, synthesizers.</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>Existing workflow state to resume from (null on initial create).</summary>
    public IvrWorkflowState? RestoreFrom { get; init; }
}

public enum StrategyKind
{
    RealtimeVoice,
    SttTts,
    Nlu,
    Dtmf,
    AgentEnsemble,
    Composite
}

/// <summary>
/// Discriminated event emitted by a strategy. Observers and the session pattern-match
/// on the concrete record.
/// </summary>
public abstract record StrategyEvent(DateTimeOffset At)
{
    public sealed record Transcript(string Speaker, string Text, bool IsFinal, DateTimeOffset At) : StrategyEvent(At);
    public sealed record AgentUtterance(string AgentId, string Text, DateTimeOffset At) : StrategyEvent(At);
    public sealed record AudioPlayed(string audioFileId, DateTimeOffset At) : StrategyEvent(At);
    public sealed record AgentSpeakingChanged(string AgentId, string? AgentDisplayName, DateTimeOffset At) : StrategyEvent(At);
    public sealed record DelegateInsight(string AgentId, string Insight, double? Confidence, DateTimeOffset At) : StrategyEvent(At);
    public sealed record FunctionCalled(string Name, IReadOnlyDictionary<string, object?> Arguments, DateTimeOffset At) : StrategyEvent(At);
    public sealed record DtmfRecognized(string Digits, string? StepId, DateTimeOffset At) : StrategyEvent(At);
    public sealed record WorkflowStepEntered(string StepId, DateTimeOffset At) : StrategyEvent(At);
    public sealed record IntentClassified(string Intent, double Confidence, DateTimeOffset At) : StrategyEvent(At);
    public sealed record EscalationRequested(string Reason, DateTimeOffset At) : StrategyEvent(At);
    public sealed record TierDegraded(AgentTier From, AgentTier To, string Reason, DateTimeOffset At) : StrategyEvent(At);
    public sealed record Faulted(string Message, Exception? Exception, DateTimeOffset At) : StrategyEvent(At);

    /// <summary>
    /// Emitted by the session when the active edge dropped a directive whose kind
    /// is not in its <see cref="EdgeCapabilities"/>. Indicates a strategy/edge
    /// mismatch — typically a strategy paired with the wrong tier of edge.
    /// </summary>
    public sealed record DispatchUnsupported(string DirectiveKind, EdgeCapabilities EdgeCapabilities, DateTimeOffset At) : StrategyEvent(At);
}
