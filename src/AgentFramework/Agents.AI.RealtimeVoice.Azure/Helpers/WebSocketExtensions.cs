using System.Net.WebSockets;
using Agents.AI.RealtimeVoice.Azure.Calling;
using Agents.AI.RealtimeVoice.Azure.Transports;
using Microsoft.Extensions.Logging;

namespace Agents.AI.RealtimeVoice.Azure.Helpers;

public static class WebSocketExtensions
{
    /// <summary>
    /// Keeps the WebSocket connection alive while the AcsWebsocketChannel processes the stream
    /// </summary>
    private static async Task KeepWebSocketAliveAsync(
        this WebSocket webSocket,
        ContactCenterConversationSession session,
        AcsWebsocketTransport acsChannel,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource();

        // Monitor WebSocket state
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested &&
                       webSocket.State == WebSocketState.Open)
                {
                    await Task.Delay(1000, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
            finally
            {
                tcs.TrySetResult();
            }
        }, cancellationToken);

        // Wait for WebSocket to close or cancellation
        await tcs.Task;

        logger.LogInformation(
            "WebSocket closed for channel {ChannelId}. State: {State}, CloseStatus: {CloseStatus}",
            acsChannel.Metadata.ContactId,
            webSocket.State,
            webSocket.CloseStatus);
    }
}
