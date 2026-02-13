using System.Net.WebSockets;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.RealtimeVoice.Azure.Calling.Transports;
using Azure.Communication.CallAutomation;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

public static class ContactCenterConversationSessionExtensions
{

    public static async Task<HubSessionParticipantContext> AddChatAIAgentAsync(
    this ContactCenterConversationSession session,
    string participantId,
    string? displayName = null,
    Func<IServiceProvider, Task<AIAgent>>? agentFactory = null,
    Func<AIAgent, AgentThread>? createThreadOverride = null,
    Action<AgentRunOptions>? configureRunOptions = null)
    {
        var participant = session.GetOrAddParticipant(participantId, displayName);

        await session.AddTransportToParticipant(participantId, async sp =>
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
    public static async Task<HubSessionParticipantContext> AddRealtimeAIAgentAsync(
        this ContactCenterConversationSession session,
        string participantId,
        string? displayName = null,
        Action<RealtimeAgentRunOptions>? configureRunOptions = null,
        Func<AuthorizingRealtimeAIAgent, Task<LiveConversationAgentSession>>? createThreadOverride = null)
    {
        var createThread = createThreadOverride ?? (async (agent) => await agent.GetNewSessionAsync());
        var participant = session.GetOrAddParticipant(participantId, displayName);

        await session.AddTransportToParticipant(participantId, async sp =>
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
    public static async Task<HubSessionParticipantContext> AddWorkflowRealtimeAgent(
        this ContactCenterConversationSession session,
        string participantId,
        RealtimeIvrWorkflowDefinition workflowDefinition,
        string? displayName = null,
        Action<AgentRunOptions>? configureRunOptions = null,
        Func<AuthorizingRealtimeAIAgent, Task<LiveConversationAgentSession>>? createThreadOverride = null)
    {
        var createThread = createThreadOverride ?? (async (agent) => await agent.GetNewSessionAsync());
        var participant = session.GetOrAddParticipant(participantId, displayName);

        await session.AddTransportToParticipant(participantId, async sp =>
        {
            var baseAgent = sp.GetRequiredService<AuthorizingRealtimeAIAgent>();
            var thread = await createThread(baseAgent);
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

            AgentRunOptions? runOptions = null;
            if (configureRunOptions is not null)
            {
                runOptions = new AgentRunOptions();
                configureRunOptions(runOptions);
            }

            return new WorkflowAwareRealtimeAIAgentTransport(
                baseAgent,
                thread,
                workflowDefinition,
                runOptions,
                loggerFactory);
        });
        return participant;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="session"></param>
    /// <param name="webSocket"></param>
    /// <param name="callConnectionId">From Websocket header `x-ms-call-connection-id`</param>
    /// <returns></returns>
    public static async Task<HubSessionParticipantContext> AddAcsWebsocketConnectionAsync(
        this ContactCenterConversationSession session,
        WebSocket webSocket,
        string callConnectionId,
        CancellationToken cancellationToken = default)
    {
        var callAutomationClient = session.HubSessionContext.CallAutomation;

        var callConnection = callAutomationClient.GetCallConnection(callConnectionId);
        var callInfo = await callConnection.GetCallConnectionPropertiesAsync(cancellationToken);

        var callerPhoneNumber = callInfo.Value.SourceCallerIdNumber?.PhoneNumber ?? callInfo.Value.Source.RawId;
        var participant = session.GetOrAddParticipant(callerPhoneNumber, callInfo.Value.SourceDisplayName);

        await session.AddTransportToParticipant(callerPhoneNumber, async sp =>
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
    /// Creates a filtered subscription to the session's <see cref="SessionContextBus"/>.
    /// Subscribers receive structured context events (transcripts, chat, CRM data,
    /// modality insights, approval decisions) without interfering with real-time audio routing.
    /// </summary>
    /// <param name="session">The conversation session.</param>
    /// <param name="filter">
    /// Optional predicate to filter events. Pass null to receive all events.
    /// Example: <c>e => e.Kind is ContextEventKind.ModalityInsight</c>
    /// </param>
    /// <returns>A <see cref="ContextSubscription"/> that can be read as an async stream.</returns>
    public static ContextSubscription SubscribeToContext(
        this ContactCenterConversationSession session,
        Func<SessionContextEvent, bool>? filter = null)
    {
        return session.ContextBus.Subscribe(filter);
    }

    /// <summary>
    /// Publishes a context event to the session's <see cref="SessionContextBus"/>.
    /// This is non-blocking and does not affect real-time audio routing.
    /// </summary>
    public static ValueTask PublishContextAsync(
        this ContactCenterConversationSession session,
        SessionContextEvent contextEvent,
        CancellationToken cancellationToken = default)
    {
        return session.ContextBus.PublishAsync(contextEvent, cancellationToken);
    }

    /// <summary>
    /// Adds a modality processor (e.g., screen analysis AI, document analysis AI) that
    /// subscribes to a <see cref="RawMediaStreamChannel"/> data stream and publishes
    /// <see cref="ContextEventKind.ModalityInsight"/> events to the session's context bus.
    /// <para>
    /// The processor runs as a background participant. The primary voice AI agent
    /// can subscribe to the context bus to receive these insights as additional context.
    /// </para>
    /// </summary>
    /// <param name="session">The conversation session.</param>
    /// <param name="participantId">Unique participant identifier for the processor.</param>
    /// <param name="processor">The modality processor implementation.</param>
    /// <param name="sourceDataChannel">
    /// The <see cref="RawMediaStreamChannel"/> producing the data stream to analyze
    /// (e.g., screen share frames, document pages).
    /// </param>
    /// <param name="displayName">Optional display name.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    public static async Task<HubSessionParticipantContext> AddModalityProcessorAsync(
        this ContactCenterConversationSession session,
        string participantId,
        IModalityProcessor processor,
        RawMediaStreamChannel sourceDataChannel,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        var participant = session.GetOrAddParticipant(participantId, displayName ?? processor.Name);
        var subscription = sourceDataChannel.Subscribe();

        await processor.StartAsync(
            subscription,
            session.ContextBus,
            session.SessionId,
            cancellationToken);

        return participant;
    }
}
