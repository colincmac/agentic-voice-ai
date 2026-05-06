using System.Threading.Channels;
using Agents.AI.Extensions.LiveVoice.Media.Signaling;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed;

// SKETCH — proposed replacement for the "real wire" half of IChannelTransport.
// Owns a socket and nothing else. No workflow, no AI, no DI scope.
//
// Today's wire-owning transports (AcsWebsocketTransport, SignalRTransport,
// GrpcTransport) collapse into ICallEdge implementations. The "AI brain"
// transports (RealtimeVoiceAgentTransport, SttTtsAgentTransport, NluIntentTransport,
// DtmfIvrTransport, ChatAIAgentTransport) become IConversationStrategy instead.

/// <summary>
/// One end of an active call's media path (PSTN-backed ACS WebSocket today,
/// browser SignalR audio for a supervisor tomorrow, SIP for direct routing later).
/// </summary>
/// <remarks>
/// A <see cref="ICallSession"/> always has exactly one <see cref="CallEdgeKind.Caller"/>
/// edge, and may attach an optional <see cref="CallEdgeKind.Supervisor"/> edge
/// for monitor / whisper / barge-in.
/// </remarks>
public interface ICallEdge : IAsyncDisposable
{
    string EdgeId { get; }

    CallEdgeKind Kind { get; }

    CallEdgeMetadata Metadata { get; }

    bool IsConnected { get; }

    /// <summary>Inbound caller audio frames. Single reader (the session).</summary>
    ChannelReader<AudioFrame> InboundAudio { get; }

    /// <summary>Inbound DTMF tones (RFC 2833 normalized by ACS). Single reader.</summary>
    ChannelReader<DtmfTone> InboundDtmf { get; }

    /// <summary>Inbound non-media signals (VAD, hold, hangup intent, etc.).</summary>
    ChannelReader<SessionSignal> InboundSignals { get; }

    /// <summary>Send an outbound audio frame to the caller.</summary>
    ValueTask SendAudioAsync(AudioFrame frame, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel any in-flight outbound audio (used for VAD-triggered barge-in:
    /// caller starts talking → strategy stops the agent's current utterance).
    /// </summary>
    ValueTask StopAudioAsync(CancellationToken cancellationToken = default);

    /// <summary>Open the wire and begin populating the inbound channels.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Closed gracefully or due to error; fires exactly once.</summary>
    event Func<EdgeDisconnectedReason, ValueTask>? Disconnected;
}

public enum CallEdgeKind
{
    Caller,
    Supervisor,
    Bridged,        // remote leg after a transfer that we still observe
    Synthetic       // for test harnesses
}

public sealed record CallEdgeMetadata
{
    public required string DisplayName { get; init; }
    public required string RawIdentifier { get; init; }
    public string? CorrelationId { get; init; }
    public string? ServerCallId { get; init; }
    public AudioFormat InboundFormat { get; init; } = AudioFormat.Pcm16Khz16BitMono;
    public AudioFormat OutboundFormat { get; init; } = AudioFormat.Pcm16Khz16BitMono;
}

public enum AudioFormat
{
    Pcm16Khz16BitMono,
    Pcm24Khz16BitMono,
    Pcm8Khz16BitMono
}

/// <summary>
/// A single chunk of PCM audio with the timestamp the wire produced/consumed it.
/// Replaces the bare <c>ReadOnlyMemory&lt;byte&gt;</c> currently passed around so
/// observers can correlate audio across edges and against transcripts.
/// </summary>
public readonly record struct AudioFrame(
    ReadOnlyMemory<byte> Pcm,
    DateTimeOffset Timestamp,
    string? SourceEdgeId = null);

public readonly record struct DtmfTone(char Digit, DateTimeOffset Timestamp);

public enum EdgeDisconnectedReason
{
    CallerHangup,
    NetworkError,
    ServerClose,
    SessionEnded,
    Timeout,
    Faulted
}
