using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Agents.AI.RealtimeVoice.Azure.Calling;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Agents.AI.RealtimeVoice.Azure.Media;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agents.AI.RealtimeVoice.Azure.CallAutomation;

public class CallingServices([FromServices] ContactCenterConversationHub conversationHub, [FromServices] CallAutomationClient callAutomationClient, IOptions<CommunicationOptions> options, ILogger<CallingServices> logger)
{
    public CallAutomationClient CallAutomationClient { get; } = callAutomationClient;
    public IOptions<CommunicationOptions> Options { get; } = options;
    public ILogger<CallingServices> Logger { get; } = logger;
    public ContactCenterConversationHub ConversationHub { get; } = conversationHub;
}

public static class CallAutomationEndpointBuilderExtensions
{
    public const string CALLBACK_PATH = "/automation/callbacks";
    public const string HANDLE_INCOMING_PATH = "/automation/incoming";
    public const string MEDIA_STREAMING_PATH_WSS = "/automation/media/wss";

    public static void MapCallAutomation(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string path = "calling")
    {
        var routeGroup = endpoints.MapGroup(path);

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
            var webSocketManager = httpContext.RequestServices.GetRequiredService<WebSocketResourceManager>();

            var sessionId = $"call_{serverCallId}";
            var session = services.ConversationHub.GetOrCreateSession(sessionId);
            var connectionTime = DateTime.UtcNow;

            // CancellationTokenSource cancelled when this connection is superseded
            using var supersededCts = new CancellationTokenSource();

            var registrationResult = await webSocketManager.RegisterAsync(
                serverCallId,
                connectionTime,
                acceptWebSocketAsync: () => httpContext.WebSockets.AcceptWebSocketAsync(),
                onSuperseded: async supersededSocket =>
                {
                    logger.LogInformation(
                        "WebSocket for ServerCallId {ServerCallId} is being superseded",
                        serverCallId);

                    // Signal the old processing loop to stop
                    await supersededCts.CancelAsync();

                    // Gracefully close the old socket
                    if (supersededSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        try
                        {
                            await supersededSocket.CloseAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "Connection superseded by a newer one",
                                CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Error closing superseded WebSocket for {ServerCallId}", serverCallId);
                        }
                    }
                });

            if (!registrationResult.IsAccepted)
            {
                logger.LogInformation(
                    "WebSocket registration rejected for ServerCallId: {ServerCallId} (older connection)",
                    serverCallId);

                if (!httpContext.Response.HasStarted)
                {
                    httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
                }
                return;
            }

            WebSocket webSocket = registrationResult.WebSocket!;
            string? previousChannelId = null;

            try
            {
                // If this superseded an old connection, replace the transport
                HubSessionParticipant acsChannel;
                if (registrationResult.WasSuperseded)
                {
                    logger.LogInformation(
                        "Replacing ACS transport for ServerCallId: {ServerCallId}",
                        serverCallId);

                    acsChannel = await session.ReplaceAcsWebsocketConnectionAsync(
                        webSocket,
                        callConnectionId,
                        previousTransportChannelId: previousChannelId,
                        cancellationToken: httpContext.RequestAborted);
                }
                else
                {
                    acsChannel = await session.AddAcsWebsocketConnectionAsync(
                        webSocket,
                        callConnectionId,
                        httpContext.RequestAborted);
                }

                // Ensure an AI agent is attached
                var callerPhoneNumber = acsChannel.Metadata.ContactId;
                var agentParticipantId = $"agent_for_{callerPhoneNumber}";
                await session.AddRealtimeAIAgentAsync(agentParticipantId);

                // Keep alive until closed or superseded
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    httpContext.RequestAborted,
                    supersededCts.Token);

                await KeepWebSocketAliveAsync(webSocket, session, acsChannel, logger, linkedCts.Token);
            }
            catch (OperationCanceledException) when (supersededCts.IsCancellationRequested)
            {
                logger.LogInformation(
                    "WebSocket for ServerCallId {ServerCallId} was superseded; exiting gracefully",
                    serverCallId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in WebSocket handler for ServerCallId: {ServerCallId}", serverCallId);

                if (webSocket.State is WebSocketState.Open)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.InternalServerError,
                        "Internal server error",
                        CancellationToken.None);
                }
            }
            finally
            {
                await webSocketManager.UnregisterAsync(serverCallId, webSocket);
            }
        }).WithName("Call Automation - Media WebSocket");
    }
    private static async Task KeepWebSocketAliveAsync(
        WebSocket webSocket,
        ContactCenterConversationSession session,
        HubSessionParticipant acsChannel,
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
