using Agents.AI.Extensions.Helpers.Streaming;

namespace Agents.AI.RealtimeVoice.Azure.Media.Messaging;

/// <summary>
/// A transport that receives inbound messages and forwards them into the session via a registered callback.
/// </summary>
public interface IMessageConsumer
{
    /// <summary>
    /// Register an inbound message handler. The router calls this to hook messages pushed from the transport.
    /// </summary>
    void SetOnMessageReceived(Func<string, MessageUpdate, CancellationToken, Task> handler);
}
