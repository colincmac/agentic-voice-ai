using System.Net.WebSockets;
using A2A;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.LiveVoice.Media.Analysis;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.RealtimeVoice.Azure.Transports;
using Azure.Communication.CallAutomation;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.A2A;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

public static class ContactCenterConversationSessionExtensions
{
    /// <summary>
    /// Replaces the ACS WebSocket transport for a participant, or adds one if none exists.
    /// The previous transport (if any) is gracefully disposed before the new one is connected.
    /// Used when ACS reconnects a media stream with a new WebSocket for the same call.
    /// </summary>
    /// <param name="session">The conversation session.</param>
    /// <param name="webSocket">The newly accepted WebSocket.</param>
    /// <param name="callConnectionId">From WebSocket header <c>x-ms-call-connection-id</c>.</param>
    /// <param name="previousTransportChannelId">
    /// If non-null, this transport will be removed from the participant before adding the new one.
    /// </param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    public static async Task<HubSessionParticipant> ReplaceAcsWebsocketConnectionAsync(
        this ContactCenterConversationSession session,
        WebSocket webSocket,
        string callConnectionId,
        string? previousTransportChannelId = null,
        CancellationToken cancellationToken = default)
    {
        var callAutomationClient = session.HubSessionContext.CallAutomation;
        var callConnection = callAutomationClient.GetCallConnection(callConnectionId);
        var callInfo = await callConnection.GetCallConnectionPropertiesAsync(cancellationToken);

        var callerPhoneNumber = callInfo.Value.SourceCallerIdNumber?.PhoneNumber ?? callInfo.Value.Source.RawId;

        // Remove the old transport first if one was superseded
        if (previousTransportChannelId is not null)
        {
            await session.RemoveTransportFromParticipantAsync(callerPhoneNumber, previousTransportChannelId);
        }

        // Add the replacement transport
        await session.AddTransportToParticipantAsync(callerPhoneNumber, async sp =>
        {
            var client = sp.GetRequiredService<CallAutomationClient>();
            var conn = client.GetCallConnection(callConnectionId);
            var props = await conn.GetCallConnectionPropertiesAsync(cancellationToken);

            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return new AcsWebsocketTransport(
                webSocket,
                props.Value,
                cancellationToken,
                loggerFactory.CreateLogger<AcsWebsocketTransport>());
        });

        return await session.GetOrAddParticipantAsync(callerPhoneNumber, callInfo.Value.SourceDisplayName, cancellationToken);
    }

    public static async Task<HubSessionParticipant> AddChatAIAgentAsync(
    this ContactCenterConversationSession session,
    string participantId,
    string? displayName = null,
    Func<IServiceProvider, Task<AIAgent>>? agentFactory = null,
    Func<AIAgent, AgentThread>? createThreadOverride = null,
    Action<AgentRunOptions>? configureRunOptions = null, CancellationToken cancellationToken = default)
    {
        var participant = await session.GetOrAddParticipantAsync(participantId, displayName, cancellationToken);

        await session.AddTransportToParticipantAsync(participantId, async sp =>
        {
            var agent = agentFactory is null
                ? sp.GetRequiredService<AIAgent>()
                : await agentFactory(sp);

            var thread = createThreadOverride is null
                ? agent.GetNewThread()
                : createThreadOverride(agent);

            AgentRunOptions? runOptions = null;
            if (configureRunOptions is not null)
            {
                runOptions = new AgentRunOptions();
                configureRunOptions(runOptions);
            }

            return new ChatAIAgentTransport(agent, thread, runOptions);
        });

        return participant;
    }

    /// <summary>
    /// Adds a participant (AI agent) to the conversation, optionally configures AgentRunOptions, creates or overrides a thread, then attaches an AuthorizingRealtimeAgentTransport and returns the participant context. 
    /// </summary>
    /// <param name="session"></param>
    /// <param name="participantId"></param>
    /// <param name="displayName"></param>
    /// <param name="configureRunOptions"></param>
    /// <param name="createThreadOverride"></param>
    /// <returns></returns>
    public static async Task<HubSessionParticipant> AddRealtimeAIAgentAsync(
        this ContactCenterConversationSession session,
        string participantId,
        string? displayName = null,
        Action<RealtimeAgentRunOptions>? configureRunOptions = null,
        Func<AuthorizingRealtimeAIAgent, Task<LiveConversationAgentSession>>? createThreadOverride = null,
        CancellationToken cancellationToken = default)
    {
        var createThread = createThreadOverride ?? (async (agent) => await agent.GetNewSessionAsync(cancellationToken));
        var participant = await session.GetOrAddParticipantAsync(participantId, displayName, cancellationToken);

        await session.AddTransportToParticipantAsync(participantId, async sp =>
        {
            var baseAgent = sp.GetRequiredService<AuthorizingRealtimeAIAgent>();
            var thread = await createThread(baseAgent);
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var analyticsService = sp.GetService<ICallAnalyticsService>();

            RealtimeAgentRunOptions? runOptions = null;
            if (configureRunOptions is not null)
            {
                runOptions = new RealtimeAgentRunOptions();
                configureRunOptions(runOptions);
            }

            return new RealtimeAIAgentTransport(
                baseAgent,
                thread,
                runOptions,
                loggerFactory,
                analyticsService,
                session.SessionId);
        });

        await session.AddTransportToParticipantAsync(participantId, async sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

            // 1. Create the analysis transport with the session's event bus
            var analysisTransport = new ConversationAnalysisTransport(
                audioPipeline: sp.GetRequiredService<IAudioAnalysisPipeline>(),
                textAnalyzer: sp.GetRequiredService<ITextSentimentAnalyzer>(),
                eventBus: session.SessionEventBus,
                analysisWindowMs: 3_000,
                loggerFactory: loggerFactory);

            var baseAgent = sp.GetRequiredService<AuthorizingRealtimeAIAgent>();
            var thread = await createThread(baseAgent);
            var analyticsService = sp.GetService<ICallAnalyticsService>();

            RealtimeAgentRunOptions? runOptions = null;
            if (configureRunOptions is not null)
            {
                runOptions = new RealtimeAgentRunOptions();
                configureRunOptions(runOptions);
            }

            return new RealtimeAIAgentTransport(
                baseAgent,
                thread,
                runOptions,
                loggerFactory,
                analyticsService,
                session.SessionId);
        });

        return participant;
    }

    /// <summary>
    /// Adds a participant (AI agent) to the conversation, optionally configures AgentRunOptions, creates or overrides a thread, then attaches a RealtimeVoiceAgentTransport and returns the participant context. 
    /// </summary>
    //public static async Task<HubSessionParticipant> AddWorkflowRealtimeAgentAsync(
    //    this ContactCenterConversationSession session,
    //    string participantId,
    //    RealtimeIvrWorkflowDefinition workflowDefinition,
    //    string? displayName = null,
    //    Action<AgentRunOptions>? configureRunOptions = null,
    //    Func<AuthorizingRealtimeAIAgent, Task<LiveConversationAgentSession>>? createThreadOverride = null,
    //    CancellationToken cancellationToken = default)
    //{
    //    var createThread = createThreadOverride ?? (async (agent) => await agent.GetNewSessionAsync(cancellationToken));
    //    var participant = await session.GetOrAddParticipantAsync(participantId, displayName, cancellationToken);

    //    await session.AddTransportToParticipantAsync(participantId, async sp =>
    //    {
    //        var baseAgent = sp.GetRequiredService<AuthorizingRealtimeAIAgent>();
    //        var thread = await createThread(baseAgent);
    //        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

    //        AgentRunOptions? runOptions = null;
    //        if (configureRunOptions is not null)
    //        {
    //            runOptions = new AgentRunOptions();
    //            configureRunOptions(runOptions);
    //        }

    //        return new RealtimeVoiceAgentTransport(
    //            baseAgent,
    //            thread,
    //            runOptions,
    //            presenceDetector: null,
    //            loggerFactory);
    //    });
    //    return participant;
    //}

    /// <summary>
    /// 
    /// </summary>
    /// <param name="session"></param>
    /// <param name="webSocket"></param>
    /// <param name="callConnectionId">From Websocket header `x-ms-call-connection-id`</param>
    /// <returns></returns>
    public static async Task<HubSessionParticipant> AddAcsWebsocketConnectionAsync(
        this ContactCenterConversationSession session,
        WebSocket webSocket,
        string callConnectionId,
        CancellationToken cancellationToken = default)
    {
        var callAutomationClient = session.HubSessionContext.CallAutomation;

        var callConnection = callAutomationClient.GetCallConnection(callConnectionId);
        var callInfo = await callConnection.GetCallConnectionPropertiesAsync(cancellationToken);

        var callerPhoneNumber = callInfo.Value.SourceCallerIdNumber?.PhoneNumber ?? callInfo.Value.Source.RawId;
        var participant = await session.GetOrAddParticipantAsync(callerPhoneNumber, callInfo.Value.SourceDisplayName, cancellationToken);

        await session.AddTransportToParticipantAsync(callerPhoneNumber, async sp =>
        {
            var callAutomationClient = sp.GetRequiredService<CallAutomationClient>();
            var callConnection = callAutomationClient.GetCallConnection(callConnectionId);
            var callInfo = await callConnection.GetCallConnectionPropertiesAsync(cancellationToken);

            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return new AcsWebsocketTransport(webSocket, callInfo.Value, cancellationToken, loggerFactory.CreateLogger<AcsWebsocketTransport>());
        });
        return participant;
    }

    /// <summary>
    /// Creates a filtered subscription to the session's <see cref="HubSessionEventBus"/>.
    /// Subscribers receive structured context events (transcripts, chat, CRM data,
    /// agent insights, approval decisions) without interfering with real-time audio routing.
    /// </summary>
    /// <param name="session">The conversation session.</param>
    /// <param name="filter">
    /// Optional predicate to filter events. Pass null to receive all events.
    /// Example: <c>e => e.Kind is HubSessionEventKind.AgentInsight</c>
    /// </param>
    /// <returns>A <see cref="SessionContextSubscription"/> that can be read as an async stream.</returns>
    public static SessionContextSubscription SubscribeToContext(
        this ContactCenterConversationSession session,
        Func<SessionContextEvent, bool>? filter = null)
    {
        return session.SessionEventBus.Subscribe(filter);
    }

    /// <summary>
    /// Publishes a context event to the session's <see cref="HubSessionEventBus"/>.
    /// This is non-blocking and does not affect real-time audio routing.
    /// </summary>
    public static ValueTask PublishContextAsync(
        this ContactCenterConversationSession session,
        SessionContextEvent contextEvent,
        CancellationToken cancellationToken = default)
    {
        return session.SessionEventBus.PublishAsync(contextEvent, cancellationToken);
    }

    /// <summary>
    /// Adds an A2A (Agent-to-Agent) agent as a participant in the conversation.
    /// The A2A agent communicates via messages — it receives transcripts/messages
    /// from other participants and sends its responses back into the session.
    /// Responses are also published to the <see cref="HubSessionEventBus"/> as
    /// <see cref="HubSessionEventKind.AgentInsight"/> events.
    /// </summary>
    /// <param name="session">The conversation session.</param>
    /// <param name="participantId">Unique participant identifier for the agent.</param>
    /// <param name="agent">The <see cref="AIAgent"/> to add (typically an A2A-backed agent).</param>
    /// <param name="displayName">Optional display name.</param>
    /// <param name="configureRunOptions">Optional callback to configure <see cref="AgentRunOptions"/>.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    public static async Task<HubSessionParticipant> AddA2AAgentAsync(
        this ContactCenterConversationSession session,
        string participantId,
        AIAgent agent,
        string? displayName = null,
        Action<AgentRunOptions>? configureRunOptions = null,
        CancellationToken cancellationToken = default)
    {
        var participant = await session.GetOrAddParticipantAsync(participantId, displayName ?? agent.Name, cancellationToken);

        await session.AddTransportToParticipantAsync(participantId, sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

            var thread = (A2AAgentThread)agent.GetNewThread();

            AgentRunOptions? runOptions = null;
            if (configureRunOptions is not null)
            {
                runOptions = new AgentRunOptions();
                configureRunOptions(runOptions);
            }

            return Task.FromResult<IChannelTransport>(new A2AAgentTransport(
                agent,
                thread,
                runOptions,
                session.SessionEventBus,
                loggerFactory));
        });

        return participant;
    }


}
