using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Transports;

/// <summary>
/// Abstraction for a bidirectional channel transport (SignalR, gRPC, WebSocket, etc.).
/// </summary>
public interface IChannelTransport : IAsyncDisposable
{
    /// <summary>Underlying contact/channel identity.</summary>
    string ChannelId { get; }

    bool IsConnected { get; }

    /// <summary>Returns metadata describing capabilities.</summary>
    ParticipantTransportMetadata Metadata { get; }

    /// <summary>Send a discrete audio frame (raw PCM or encoded) to the transport.</summary>
    Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default);

    /// <summary>Send a message envelope (already serialized or domain object).</summary>
    Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Register an inbound audio handler. The router calls this to hook audio pushed from the transport.
    /// Transport implementation invokes callback for every received frame.
    /// </summary>
    void OnAudioReceived(Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> handler);

    /// <summary>
    /// Register an inbound message handler.
    /// </summary>
    void OnMessageReceived(Func<string, MessageUpdate, CancellationToken, Task> handler);

    /// <summary>
    /// Register a callback invoked exactly once when the transport becomes disconnected (gracefully or due to error).
    /// Implementations should attempt to invoke this prior to disposal, but must ensure it fires even on Dispose.
    /// </summary>
    void OnDisconnected(Func<string, Task> handler);

    Task ConnectAsync(CancellationToken cancellationToken = default);
}


