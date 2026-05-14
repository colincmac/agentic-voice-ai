using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text.Json;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Implementation;
using Agents.AI.ContactCenter.Configuration;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;

namespace Showcase.Agent.VoiceAgent.Apis;

/// <summary>
/// ACS Call Automation endpoints. Supports two answer modes:
/// <list type="bullet">
///   <item><b>streaming</b> (default) — answers with bidirectional media WebSocket;
///         WS handler builds <see cref="AcsCallerEdge"/> and starts the session.</item>
///   <item><b>verb</b> (?mode=verb) — answers with no media WS; IncomingCall handler
///         builds <see cref="AcsCallAutomationEdge"/> and starts the session immediately.
///         Subsequent caller actions arrive on the callback webhook below.</item>
/// </list>
/// </summary>
public static class CallingApi
{
    public const string HANDLE_INCOMING_PATH = "/automation/incoming";
    public const string CALLBACK_PATH = "/automation/callbacks";
    public const string MEDIA_STREAMING_PATH_WSS = "/automation/media/wss";

    public static void MapCallAutomation(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string path = "calling")
    {
        var routeGroup = endpoints.MapGroup(path).AllowAnonymous();

        routeGroup.MapPost(HANDLE_INCOMING_PATH, async (
            [AsParameters] CallingServices services,
            [FromBody] EventGridEvent[] incomingEvents,
            [FromQuery] string? mode = "streaming",
            CancellationToken cancellationToken = default) =>
        {
            foreach (var evt in incomingEvents)
            {
                if (!evt.TryGetSystemEventData(out var eventData))
                {
                    continue;
                }

                if (eventData is SubscriptionValidationEventData subscriptionValidation)
                {
                    return Results.Ok(new SubscriptionValidationResponse
                    {
                        ValidationResponse = subscriptionValidation.ValidationCode
                    });
                }

                if (eventData is AcsIncomingCallEventData incoming)
                {
                    services.Logger.LogInformation(
                        "Incoming call from {From} to {To} (server call {ServerCallId}); mode={Mode}",
                        incoming.FromCommunicationIdentifier?.RawId,
                        incoming.ToCommunicationIdentifier?.RawId,
                        incoming.ServerCallId,
                        mode);

                    var callbackUri = new Uri(
                        services.Options.Value.Acs.CallBackUri,
                        relativeUri: $"{path}{CALLBACK_PATH}/{incoming.ServerCallId}");

                    var answerOptions = new AnswerCallOptions(incoming.IncomingCallContext, callbackUri);

                    if (mode != "verb")
                    {
                        var websocketUri = new Uri(
                            services.Options.Value.Acs.MediaStreamingUri,
                            relativeUri: $"{path}{MEDIA_STREAMING_PATH_WSS}/{incoming.ServerCallId}");

                        answerOptions.MediaStreamingOptions = new MediaStreamingOptions(
                            audioChannelType: MediaStreamingAudioChannel.Mixed,
                            streamingTransport: StreamingTransport.Websocket)
                        {
                            EnableBidirectional = true,
                            EnableDtmfTones = true,
                            TransportUri = websocketUri,
                            StartMediaStreaming = true,
                            AudioFormat = services.Options.Value.Acs.AudioFormat
                        };
                    }

                    var answerResult = await services.CallAutomationClient
                        .AnswerCallAsync(answerOptions, cancellationToken).ConfigureAwait(false);

                    var callConnection = answerResult.Value.CallConnection;

                    services.Logger.LogInformation(
                        "Answered call. CallConnectionId: {CallConnectionId}",
                        callConnection.CallConnectionId);

                    // Kick off strategy prewarm right after answering so the realtime backend
                    // connect / first-prompt TTS overlap with ACS opening the media channel.
                    // CreateAsync below will claim this prewarmed entry by callId.
                    var prewarmCallId = $"call_{callConnection.CallConnectionId}";
                    var prewarmTier = AgentTier.RealtimeVoice;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await services.SessionFactory.PrewarmAsync(new CallSessionPrewarmRequest
                            {
                                CallId = prewarmCallId,
                                Workflow = services.Workflow,
                                PreferredTier = prewarmTier
                            }, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            services.Logger.LogWarning(ex,
                                "Strategy prewarm failed for call {CallId}; CreateAsync will build a fresh strategy",
                                prewarmCallId);
                        }
                    }, CancellationToken.None);

                    if (mode == "verb")
                    {
                        // Verb-mode session is born here — no WS handshake will follow.
                        await StartVerbSessionAsync(services, callConnection, incoming, cancellationToken);
                    }
                    // Streaming mode waits for the media WSS handler below to build the edge.
                }
            }

            return Results.Ok();
        }).WithName("Call Automation - HandleIncomingCall");

        routeGroup.MapPost("/automation/callbacks/{serverCallId}", async (
            [AsParameters] CallingServices services,
            [FromBody] CloudEvent[] cloudEvents,
            [FromRoute] string serverCallId) =>
        {
            foreach (var cloudEvent in cloudEvents)
            {
                var callAutomationEvent = CallAutomationEventParser.Parse(cloudEvent);
                services.Logger.LogDebug("Call event {Type} for {ServerCallId}",
                    callAutomationEvent.GetType().Name, serverCallId);

                // Find the session and dispatch to the verb edge if the call uses one.
                var callId = $"call_{callAutomationEvent.CallConnectionId}";
                var session = services.SessionRegistry.TryGet(callId);
                if (session?.CallerEdge is AcsCallAutomationEdge verbEdge)
                {
                    DispatchToVerbEdge(verbEdge, callAutomationEvent);
                }
            }
            return Results.Ok();
        }).WithName("Call Automation - HandleCallEvents");

        routeGroup.MapGet("/automation/media/wss/{serverCallId}", async (
            HttpContext httpContext,
            [AsParameters] CallingServices services,
            [FromRoute] string serverCallId,
            [FromHeader(Name = "x-ms-call-connection-id")] string callConnectionId) =>
        {
            if (!httpContext.WebSockets.IsWebSocketRequest)
            {
                httpContext.Response.StatusCode = 400;
                return;
            }

            var loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("CallAutomation.WebSocket");

            WebSocket? webSocket = null;
            try
            {
                webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();
                logger.LogInformation(
                    "Media WebSocket established for ServerCallId={ServerCallId}, CallConnectionId={CallConnectionId}, Tier={Tier}",
                    serverCallId, callConnectionId, AgentTier.RealtimeVoice);

                var callConnection = services.CallAutomationClient.GetCallConnection(callConnectionId);
                var callProperties = (await callConnection.GetCallConnectionPropertiesAsync(httpContext.RequestAborted)).Value;

                var edge = new AcsCallerEdge(
                    webSocket,
                    callProperties,
                    httpContext.RequestAborted,
                    services.CallAutomationClient,
                    loggerFactory.CreateLogger<AcsCallerEdge>());

                var callId = $"call_{callConnectionId}";

                var session = await services.SessionFactory.CreateAsync(new CallSessionRequest
                {
                    CallId = callId,
                    CallerEdge = edge,
                    Workflow = services.Workflow,
                    PreferredTier = AgentTier.RealtimeVoice
                }, httpContext.RequestAborted);

                await session.StartAsync(httpContext.RequestAborted);
                logger.LogInformation("Streaming call session {CallId} started ({Tier})", callId, AgentTier.RealtimeVoice);

                await WaitForCallEndAsync(session, httpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Media WebSocket failed for ServerCallId={ServerCallId}", serverCallId);
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

    /// <summary>
    /// Verb-mode call-start path. Answer has already happened; we wrap the
    /// <see cref="CallConnection"/> in <see cref="AcsCallAutomationEdge"/>, hand it
    /// to the session factory, and start the session. The session lives until the
    /// callback webhook posts <c>CallDisconnected</c>.
    /// </summary>
    private static async Task StartVerbSessionAsync(
        CallingServices services,
        CallConnection callConnection,
        AcsIncomingCallEventData incoming,
        CancellationToken cancellationToken)
    {
        // Recognize verbs target the calling participant.
        var fromIdentifier = CommunicationIdentifier.FromRawId(
            incoming.FromCommunicationIdentifier!.RawId);

        var media = new CallMediaClient(callConnection, fromIdentifier);
        var control = new CallControlClient(callConnection);
        var metadata = new CallEdgeMetadata
        {
            DisplayName = incoming.FromCommunicationIdentifier!.RawId,
            RawIdentifier = incoming.FromCommunicationIdentifier!.RawId,
            CorrelationId = incoming.CorrelationId,
            ServerCallId = incoming.ServerCallId,
        };

        var edge = new AcsCallAutomationEdge(
            callConnection.CallConnectionId,
            media,
            metadata,
            control,
            services.LoggerFactory.CreateLogger<AcsCallAutomationEdge>());

        var callId = $"call_{callConnection.CallConnectionId}";

        var session = await services.SessionFactory.CreateAsync(new CallSessionRequest
        {
            CallId = callId,
            CallerEdge = edge,
            Workflow = services.Workflow,
            PreferredTier = AgentTier.RealtimeVoice,
        }, cancellationToken);

        await session.StartAsync(cancellationToken);
        services.Logger.LogInformation("Verb-mode call session {CallId} started ({Tier})", callId, AgentTier.RealtimeVoice);
    }

    private static void DispatchToVerbEdge(AcsCallAutomationEdge edge, CallAutomationEventBase evt)
    {
        switch (evt)
        {
            case RecognizeCompleted rc:
                edge.OnRecognizeCompleted(rc);
                break;
            case RecognizeFailed rf:
                edge.OnRecognizeFailed(rf);
                break;
            case PlayCompleted pc:
                edge.OnPlayCompleted(pc);
                break;
            case PlayFailed pf:
                edge.OnPlayFailed(pf);
                break;
            case CallDisconnected:
                edge.OnCallDisconnected();
                break;
        }
    }

    /// <summary>
    /// Parks the request until the session reaches a terminal state. Used by the
    /// streaming WSS handler — verb mode doesn't need this since no request is parked.
    /// </summary>
    private static async Task WaitForCallEndAsync(ICallSession session, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ValueTask Handler(CallSessionState state)
        {
            if (state is CallSessionState.Ended or CallSessionState.Faulted)
            {
                tcs.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }

        session.StateChanged += Handler;
        try
        {
            using var registration = cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), tcs);

            if (session.State is CallSessionState.Ended or CallSessionState.Faulted)
            {
                return;
            }

            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            session.StateChanged -= Handler;
        }
    }
}
