using Agents.AI.RealtimeVoice.Azure.Media.Audio;
using Agents.AI.RealtimeVoice.Azure.Media.Messaging;
using Agents.AI.RealtimeVoice.Azure.Models;

namespace Agents.AI.RealtimeVoice.Azure.Transports;

/// <summary>
/// Lean base abstraction for a channel transport (SignalR, gRPC, WebSocket, etc.).
/// <para>
/// Media capabilities are expressed through optional interface implementations rather
/// than methods on this base interface. A transport declares what it can handle by
/// additionally implementing one or more of:
/// <list type="bullet">
///   <item><see cref="IAudioProducer"/> / <see cref="IAudioConsumer"/> – raw audio frames</item>
///   <item><see cref="IMessageProducer"/> / <see cref="IMessageConsumer"/> – structured messages</item>
/// </list>
/// Consumers (e.g., session routers, participants) check for these interfaces at
/// runtime to decide what content to route through each transport.
/// </para>
/// </summary>
public interface IChannelTransport : IAsyncDisposable
{
    /// <summary>Underlying contact/channel identity.</summary>
    string ChannelId { get; }

    /// <summary>Whether the transport is currently connected and able to send/receive.</summary>
    bool IsConnected { get; }

    /// <summary>Returns metadata describing capabilities and identity.</summary>
    ParticipantTransportMetadata Metadata { get; }

    /// <summary>
    /// Register a callback invoked exactly once when the transport becomes disconnected
    /// (gracefully or due to error). Implementations must ensure it fires even on Dispose.
    /// </summary>
    void SetOnDisconnected(Func<string, Task> handler);

    /// <summary>Open the transport connection and start any background loops.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);
}


