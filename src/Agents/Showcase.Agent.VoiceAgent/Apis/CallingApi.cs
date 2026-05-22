using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text.Json;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Core;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Coordination.Core;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;

namespace Showcase.Agent.VoiceAgent.Apis;

/// <summary>
/// ACS Call Automation endpoints. The answer mode is selected by the registered
/// <see cref="RealtimeIvrWorkflowDefinition"/>'s <see cref="AgentTier"/>:
/// <list type="bullet">
///   <item><see cref="AgentTier.RealtimeVoice"/> — answers with a bidirectional media WebSocket;
///         the WSS handler builds <see cref="AcsCallerStreamEdge"/> and starts the session.</item>
///   <item>All other tiers — answers with no media WS; the IncomingCall handler builds
///         <see cref="AcsCallAutomationEdge"/> and starts the session immediately. Subsequent
///         caller actions arrive on the callback webhook below.</item>
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
            CancellationToken cancellationToken = default) =>
        {
            // Tier comes from the registered workflow definition (driven by YAML
            // strategy.primary). RealtimeVoice ⇒ open a bidirectional media WS so the
            // streaming WSS handler can build an AcsCallerStreamEdge. Any other tier
            // ⇒ answer in verb mode and build an AcsCallAutomationEdge here.
            var workflowTier = services.Workflow.Tier;
            var useStreaming = true;

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
                        "Incoming call from {From} to {To} (server call {ServerCallId}); workflow={Workflow} tier={Tier}",
                        incoming.FromCommunicationIdentifier?.RawId,
                        incoming.ToCommunicationIdentifier?.RawId,
                        incoming.ServerCallId,
                        services.Workflow.Name,
                        workflowTier);

                    var callbackUri = new Uri(
                        services.Options.Value.Acs.CallBackUri,
                        relativeUri: $"{path}{CALLBACK_PATH}/{incoming.ServerCallId}");

                    var answerOptions = new AnswerCallOptions(incoming.IncomingCallContext, callbackUri);

                    if (useStreaming)
                    {
                        var websocketUri = new Uri(
                            services.Options.Value.Acs.MediaStreamingUri,
                            relativeUri: $"{path}{MEDIA_STREAMING_PATH_WSS}");

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

                    if (!useStreaming)
                    {
                        // Claim ownership before the verb edge starts so the very first
                        // mid-call callback can find this pod (ADR-0011).
                        var claim = await services.Ownership.TryAcquireAsync(
                            callConnection.CallConnectionId,
                            CallOwnershipKind.Verb,
                            cancellationToken).ConfigureAwait(false);

                        if (!claim.Acquired)
                        {
                            services.Logger.LogWarning(
                                "Verb-mode ownership claim refused for {CallConnectionId}; existing owner is {OwnerCluster}/{OwnerPod}",
                                callConnection.CallConnectionId, claim.Owner.ClusterId, claim.Owner.PodId);
                        }

                        // Verb-mode session is born here — no WS handshake will follow.
                        await StartVerbSessionAsync(services, callConnection, incoming, cancellationToken);
                    }
                    // Streaming mode waits for the media WSS handler below to build the edge.
                }
            }

            return Results.Ok();
        }).WithName("Call Automation - HandleIncomingCall");

        routeGroup.MapPost("/automation/callbacks/{serverCallId}", async (
            HttpContext httpContext,
            [AsParameters] CallingServices services,
            [FromRoute] string serverCallId,
            CancellationToken cancellationToken) =>
        {
            return await HandleCallbackAsync(
                httpContext,
                services,
                serverCallId,
                isForwarded: false,
                cancellationToken).ConfigureAwait(false);
        }).WithName("Call Automation - HandleCallEvents");

        // Forwarded receive endpoint for cross-pod dispatch per ADR-0011. The
        // non-owning pod replays the original webhook body verbatim; this route
        // skips the owner lookup and processes locally. Loop protection: refuse
        // any request that carries our own InstanceId in X-Forwarded-By-Instance.
        routeGroup.MapPost("/automation/callbacks/_forwarded/{serverCallId}", async (
            HttpContext httpContext,
            [AsParameters] CallingServices services,
            [FromRoute] string serverCallId,
            CancellationToken cancellationToken) =>
        {
            var forwardedBy = httpContext.Request.Headers[HttpWebhookForwarder.ForwardedByHeader].ToString();
            if (string.Equals(forwardedBy, services.ClusterIdentity.InstanceId, StringComparison.Ordinal))
            {
                services.Logger.LogWarning(
                    "Refusing forwarded webhook that originated from this pod (loop guard); InstanceId={InstanceId}",
                    services.ClusterIdentity.InstanceId);
                return Results.StatusCode(StatusCodes.Status421MisdirectedRequest);
            }

            return await HandleCallbackAsync(
                httpContext,
                services,
                serverCallId,
                isForwarded: true,
                cancellationToken).ConfigureAwait(false);
        }).WithName("Call Automation - HandleForwardedCallEvents");

        routeGroup.MapGet("/automation/media/wss", async (
            HttpContext httpContext,
            [AsParameters] CallingServices services,
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
            var callConnectionIdForOwnership = callConnectionId;
            var ownershipClaimed = false;
            try
            {
                webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();
                logger.LogInformation(
                    "Media WebSocket established for CallConnectionId={CallConnectionId}, Tier={Tier}",
                    callConnectionId, services.Workflow.Tier);

                // Pod-pinned bi-di stream — claim streaming ownership before the
                // session starts so the very first mid-call callback can find us.
                var claim = await services.Ownership.TryAcquireAsync(
                    callConnectionIdForOwnership,
                    CallOwnershipKind.Streaming,
                    httpContext.RequestAborted).ConfigureAwait(false);

                if (!claim.Acquired)
                {
                    logger.LogError(
                        "Streaming ownership claim refused for {CallConnectionId}; existing owner {OwnerCluster}/{OwnerPod} — closing WS",
                        callConnectionId, claim.Owner.ClusterId, claim.Owner.PodId);
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "Call already owned",
                        CancellationToken.None);
                    return;
                }

                ownershipClaimed = true;

                var callConnection = services.CallAutomationClient.GetCallConnection(callConnectionId);
                var callProperties = (await callConnection.GetCallConnectionPropertiesAsync(httpContext.RequestAborted)).Value;

                var edge = new AcsCallerStreamEdge(
                    webSocket,
                    callProperties,
                    httpContext.RequestAborted,
                    services.CallAutomationClient,
                    loggerFactory.CreateLogger<AcsCallerStreamEdge>(),
                    services.Telemetry);

                var callId = $"call_{callConnectionId}";
                var session = await StartCallSessionAsync(
                    services, callId, edge, logger, "Streaming", httpContext.RequestAborted)
                    .ConfigureAwait(false);

                await WaitForCallEndAsync(session, httpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Media WebSocket failed for CallConnectionId={CallConnectionId}", callConnectionId);
                if (webSocket?.State == WebSocketState.Open)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.InternalServerError,
                        "Internal server error",
                        CancellationToken.None);
                }
            }
            finally
            {
                if (ownershipClaimed)
                {
                    try
                    {
                        await services.Ownership.ReleaseAsync(callConnectionIdForOwnership, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception releaseEx)
                    {
                        logger.LogWarning(releaseEx,
                            "Failed to release streaming ownership for {CallConnectionId}; lease will expire via TTL",
                            callConnectionIdForOwnership);
                    }
                }
            }
        }).WithName("Call Automation - Media WebSocket");
    }

    /// <summary>
    /// Shared callback dispatch for the direct webhook route and the
    /// <c>_forwarded</c> route. Reads the raw body so it can be replayed
    /// verbatim to the WS-owning pod when the local pod is not the owner
    /// (ADR-0011). Forwarded requests skip the owner lookup since the sender
    /// already resolved it.
    /// </summary>
    private static async Task<IResult> HandleCallbackAsync(
        HttpContext httpContext,
        CallingServices services,
        string serverCallId,
        bool isForwarded,
        CancellationToken cancellationToken)
    {
        // Read once so we can both parse and (potentially) forward the body.
        byte[] bodyBytes;
        using (var ms = new MemoryStream())
        {
            await httpContext.Request.Body.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            bodyBytes = ms.ToArray();
        }

        var contentType = httpContext.Request.ContentType ?? "application/cloudevents-batch+json";

        CloudEvent[] cloudEvents;
        try
        {
            cloudEvents = CloudEvent.ParseMany(BinaryData.FromBytes(bodyBytes));
        }
        catch (Exception ex)
        {
            services.Logger.LogWarning(ex, "Failed to parse CloudEvent batch for {ServerCallId}", serverCallId);
            return Results.BadRequest();
        }

        // Group by callConnectionId so we look up the owner once per call.
        var byCall = new Dictionary<string, List<CallAutomationEventBase>>(StringComparer.Ordinal);
        foreach (var cloudEvent in cloudEvents)
        {
            var callAutomationEvent = CallAutomationEventParser.Parse(cloudEvent);
            services.Logger.LogDebug("Call event {Type} for {ServerCallId} (forwarded={Forwarded})",
                callAutomationEvent.GetType().Name, serverCallId, isForwarded);

            var ccid = callAutomationEvent.CallConnectionId;
            if (string.IsNullOrEmpty(ccid))
            {
                continue;
            }

            if (!byCall.TryGetValue(ccid, out var list))
            {
                list = new List<CallAutomationEventBase>();
                byCall[ccid] = list;
            }
            list.Add(callAutomationEvent);
        }

        foreach (var (callConnectionId, events) in byCall)
        {
            // Forwarded payloads always dispatch locally — the sender resolved the owner.
            // For first-hop deliveries, look up ownership and decide local-vs-forward.
            if (!isForwarded)
            {
                var owner = await services.Ownership.GetOwnerAsync(callConnectionId, cancellationToken)
                    .ConfigureAwait(false);

                if (owner is not null
                    && !IsLocalOwner(owner, services.ClusterIdentity)
                    && owner.Kind == CallOwnershipKind.Streaming)
                {
                    // Streaming-mode call owned by another pod — forward verbatim.
                    var forwardResult = await services.WebhookForwarder.TryForwardAsync(
                        owner,
                        callbackPath: $"{httpContext.Request.Path}{httpContext.Request.QueryString}",
                        body: bodyBytes,
                        contentType: contentType,
                        headers: null,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (forwardResult.IsSuccess)
                    {
                        continue;
                    }

                    services.Logger.LogWarning(
                        "Forward to streaming owner {OwnerPod} failed with {Outcome} ({Status}); dropping {CallConnectionId} to reaper path",
                        owner.PodId, forwardResult.Outcome, forwardResult.StatusCode, callConnectionId);
                    // Fall through to local-dispatch attempt; the reaper (ADR-0011)
                    // will polite-hangup the call if it stays orphaned.
                }
            }

            DispatchLocally(services, callConnectionId, events);

            // Release ownership once the call has ended, regardless of who answered.
            if (events.Any(e => e is CallDisconnected))
            {
                await services.Ownership.ReleaseAsync(callConnectionId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return Results.Ok();
    }

    private static void DispatchLocally(
        CallingServices services,
        string callConnectionId,
        IReadOnlyList<CallAutomationEventBase> events)
    {
        var callId = $"call_{callConnectionId}";
        var session = services.SessionRegistry.TryGet(callId);
        if (session?.CallerEdge is not AcsCallAutomationEdge verbEdge)
        {
            // Streaming-mode events for a call we don't own locally fall here only
            // when the forward attempt failed; we have no in-pod handler to drive,
            // so log and let the reaper deal with it.
            return;
        }

        foreach (var evt in events)
        {
            DispatchToVerbEdge(verbEdge, evt);
        }
    }

    private static bool IsLocalOwner(CallOwnership owner, IClusterIdentity identity)
        => string.Equals(owner.ClusterId, identity.ClusterId, StringComparison.Ordinal)
           && string.Equals(owner.PodId, identity.PodId, StringComparison.Ordinal);

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
            services.LoggerFactory.CreateLogger<AcsCallAutomationEdge>(),
            services.Telemetry,
            control);

        var callId = $"call_{callConnection.CallConnectionId}";
        await StartCallSessionAsync(
            services, callId, edge, services.Logger, "Verb-mode", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Shared session-start path for both verb and streaming modes. Builds a
    /// <see cref="CallSessionRequest"/> whose <see cref="CallSessionRequest.PreferredTier"/>
    /// matches the registered workflow's <see cref="RealtimeIvrWorkflowDefinition.Tier"/>,
    /// so the DI-resolved <c>IConversationStrategyFactory</c> for that tier (typically the
    /// composite chain registered last) is selected automatically.
    /// </summary>
    private static async Task<ICallSession> StartCallSessionAsync(
        CallingServices services,
        string callId,
        ICallEdge edge,
        ILogger logger,
        string modeLabel,
        CancellationToken cancellationToken)
    {
        var tier = services.Workflow.Tier;
        var session = await services.SessionFactory.CreateAsync(new CallSessionRequest
        {
            CallId = callId,
            CallerEdge = edge,
            Workflow = services.Workflow,
            PreferredTier = tier,
        }, cancellationToken).ConfigureAwait(false);

        await session.StartAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "{Mode} call session {CallId} started ({Tier})", modeLabel, callId, tier);
        return session;
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
