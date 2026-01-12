using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text.Json;
using Agents.AI.Extensions;
using Agents.AI.Extensions.AITools;
using Agents.AI.Extensions.SessionManagement;
using Agents.AI.RealtimeVoice.Azure.Calling;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;
using Agents.AI.RealtimeVoice.Azure.Calling.Transports;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;

namespace Showcase.Agent.VoiceAgent.Apis;

public static class CallingApi
{
    public const string CALLBACK_PATH = "/automation/callbacks";
    public const string HANDLE_INCOMING_PATH = "/automation/incoming";
    public const string MEDIA_STREAMING_PATH_WSS = "/automation/media/wss";

    public static void MapCallAutomation(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string path = "calling")
    {
        var routeGroup = endpoints.MapGroup(path).AllowAnonymous();

        routeGroup.MapPost(HANDLE_INCOMING_PATH, async (
            [AsParameters] CallingServices services,
            [FromBody] EventGridEvent[] incomingEvents,
            CancellationToken cancellationToken
            ) =>
        {
            foreach (var evt in incomingEvents)
            {
                // Handle system events
                if (evt.TryGetSystemEventData(out var eventData))
                {
                    // Handle the subscription validation event.
                    if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
                    {
                        var responseData = new SubscriptionValidationResponse
                        {
                            ValidationResponse = subscriptionValidationEventData.ValidationCode
                        };
                        return Results.Ok(responseData);
                    }
                }

                if (eventData is AcsIncomingCallEventData acsIncomingCallEventData)
                {
                    services.Logger.LogDebug("AcsIncomingCallEventData received: {EventData}", JsonSerializer.Serialize(acsIncomingCallEventData));
                    var callbackUri = new Uri(services.Options.Value.Acs.CallBackUri, relativeUri: $"{path}{CALLBACK_PATH}/{acsIncomingCallEventData.ServerCallId}");
                    services.Logger.LogDebug("Callback Url: {callbackUri}", callbackUri);

                    var websocketUri = new Uri(services.Options.Value.Acs.MediaStreamingUri, relativeUri: $"{path}{MEDIA_STREAMING_PATH_WSS}/{acsIncomingCallEventData.ServerCallId}");
                    services.Logger.LogDebug("WebSocket Url: {websocketUri}", websocketUri);

                    // Create or get session for this call
                    var sessionId = $"call_{acsIncomingCallEventData.ServerCallId}";
                    var session = services.ConversationHub.GetOrCreateSession(
                        sessionId);

                    var mediaStreamingOptions = new MediaStreamingOptions(audioChannelType: MediaStreamingAudioChannel.Mixed, streamingTransport: StreamingTransport.Websocket)
                    {
                        EnableBidirectional = true,
                        EnableDtmfTones = true,
                        TransportUri = websocketUri,
                        StartMediaStreaming = true,
                        AudioFormat = AudioFormat.Pcm24KMono
                    };

                    var options = new AnswerCallOptions(acsIncomingCallEventData.IncomingCallContext, callbackUri)
                    {
                        MediaStreamingOptions = mediaStreamingOptions,
                    };

                    AnswerCallResult answerCallResult = await services.CallAutomationClient.AnswerCallAsync(options, cancellationToken);
                    services.Logger.LogInformation($"Answered call for connection id: {answerCallResult.CallConnection.CallConnectionId}");
                    return Results.Ok();
                }
            }
            return Results.Ok();
        }).WithName("Call Automation - HandleIncomingCall");

        routeGroup.MapPost("/automation/callbacks/{serverCallId}", (
            [AsParameters] CallingServices services,
            [FromBody] CloudEvent[] cloudEvents,
            [FromRoute] string serverCallId
            ) =>
        {
            foreach (var cloudEvent in cloudEvents)
            {
                var callAutomationEvent = CallAutomationEventParser.Parse(cloudEvent);
            }

            return Results.Ok();
        }).WithName("Call Automation - HandleCallEvents");

        routeGroup.MapGet("/automation/media/wss/{serverCallId}", async (
            HttpContext httpContext,
            [AsParameters] CallingServices services,
            [FromRoute] string serverCallId,
            [FromHeader(Name = "x-ms-call-connection-id")] string callConnectionId
            ) =>
        {
            if (!httpContext.WebSockets.IsWebSocketRequest)
            {
                httpContext.Response.StatusCode = 400;
                return;
            }
            var loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();

            var logger = loggerFactory.CreateLogger("CallAutomation.WebSocket");

            WebSocket? webSocket = null;
            ContactCenterConversationSession? session = null;
            HubSessionParticipantContext? acsChannel = null;
            string? callerPhoneNumber = null;

            try
            {
                // Accept the WebSocket connection
                webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();
                logger.LogInformation("WebSocket connection established for ServerCallId: {ServerCallId}", serverCallId);

                // Get or create the conversation session
                var sessionId = $"call_{serverCallId}";
                session = services.ConversationHub.GetOrCreateSession(sessionId);

                // Get call information
                acsChannel = await session.AddAcsWebsocketConnectionAsync(webSocket, callConnectionId, httpContext.RequestAborted);

                 // Check if we need to create or reuse an AI agent
                 var agentParticipantId = $"agent_for_{callerPhoneNumber}";
                await session.AddRealtimeAIAgentAsync(agentParticipantId);

                // Keep the WebSocket connection alive until it's closed
                await KeepWebSocketAliveAsync(webSocket, session, acsChannel, logger, httpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in WebSocket handler for ServerCallId: {ServerCallId}", serverCallId);

                if (webSocket?.State == WebSocketState.Open)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.InternalServerError,
                        "Internal server error",
                        CancellationToken.None);
                }
            }
        }).WithName("Call Automation - Media WebSocket");
    }
    private static async Task KeepWebSocketAliveAsync(
        WebSocket webSocket,
        ContactCenterConversationSession session,
        HubSessionParticipantContext acsChannel,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource();
        var channelId = acsChannel.ChannelId;
        var lastHealthCheck = DateTimeOffset.UtcNow;

        // Monitor WebSocket state with health checks
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested &&
                       webSocket.State == WebSocketState.Open)
                {
                    await Task.Delay(1000, cancellationToken);

                    // Log health check every 30 seconds
                    var now = DateTimeOffset.UtcNow;
                    if ((now - lastHealthCheck).TotalSeconds >= 30)
                    {
                        logger.LogDebug(
                            "WebSocket health check for channel {ChannelId}: State={State}, SessionActive={SessionActive}",
                            channelId,
                            webSocket.State,
                            session.IsActive);
                        lastHealthCheck = now;
                    }
                }

                logger.LogInformation(
                    "WebSocket monitoring loop ended for channel {ChannelId}. State: {State}, Cancelled: {Cancelled}",
                    channelId,
                    webSocket.State,
                    cancellationToken.IsCancellationRequested);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
                logger.LogDebug("WebSocket monitoring cancelled for channel {ChannelId}", channelId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in WebSocket monitoring loop for channel {ChannelId}", channelId);
            }
            finally
            {
                tcs.TrySetResult();
            }
        }, cancellationToken);

        try
        {
            // Wait for WebSocket to close
            await tcs.Task;

        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
            logger.LogDebug("WebSocket wait cancelled for channel {ChannelId}", channelId);
            // Wait for WebSocket to close or cancellation
        }

        logger.LogInformation(
            "WebSocket closed for channel {ChannelId}. State: {State}, CloseStatus: {CloseStatus}, CloseDescription: {CloseDescription}",
            channelId,
            webSocket.State,
            webSocket.CloseStatus,
            webSocket.CloseStatusDescription);
    }
}
