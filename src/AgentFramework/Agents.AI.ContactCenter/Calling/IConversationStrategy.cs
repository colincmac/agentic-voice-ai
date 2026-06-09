using System.Threading.Channels;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Configuration;

namespace Agents.AI.ContactCenter.Calling;

// SKETCH — proposed replacement for the "AI brain" half of IChannelTransport.
//
// One IConversationStrategy per call. Implementations:
//   RealtimeVoiceStrategy        — wraps an AuthorizingRealtimeAIAgent
//   SttTtsStrategy               — STT → IChatClient → TTS
//   NluStrategy                  — STT → IvrIntentAgent → workflow
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

    /// <summary>
    /// Snapshot of the caller-edge metadata (phone number, display name, server call id).
    /// Strategies pass this into authenticators and may include it in observability events.
    /// Null only when the strategy is started without a caller edge (e.g. self-test harnesses).
    /// </summary>
    public CallEdgeMetadata? CallerMetadata { get; init; }

    /// <summary>
    /// The per-call DI scope's <see cref="IServiceProvider"/>. Used by composite/fallback
    /// strategies to resolve inner <see cref="IConversationStrategy"/> instances keyed by
    /// <see cref="AgentTier"/>, and by strategies that need to resolve per-call services
    /// (e.g. <c>CallerAuthenticationState</c>, <c>IvrWorkflowState</c>) on demand.
    /// Per-call workflow state is shared across tier swaps via the scoped
    /// <see cref="IvrWorkflowState"/> registration in the same scope.
    /// </summary>
    public required IServiceProvider Services { get; init; }
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
