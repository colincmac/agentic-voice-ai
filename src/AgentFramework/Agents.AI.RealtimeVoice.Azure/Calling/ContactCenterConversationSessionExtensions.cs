using System.Net.WebSockets;
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
}
