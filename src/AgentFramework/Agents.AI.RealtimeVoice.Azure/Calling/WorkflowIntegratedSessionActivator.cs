using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.RealtimeVoice;
using Agents.AI.RealtimeVoice.Azure.Calling.Transports;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

/// <summary>
/// Session activator that integrates the Realtime AI Agent with an IVR workflow.
/// This activator creates a workflow coordinator that controls the agent's configuration
/// based on workflow step progression.
/// </summary>
/// <remarks>
/// <para>
/// The workflow coordinator runs alongside the realtime agent stream, analyzing conversation
/// turns and updating the agent's prompt and available tools when step transitions occur.
/// </para>
/// <para>
/// This approach ensures:
/// <list type="bullet">
/// <item>The AI agent only has access to tools appropriate for the current step</item>
/// <item>The agent's behavior (via prompts) changes as the workflow progresses</item>
/// <item>Transitions between steps are gated by guards (e.g., authentication level, required data)</item>
/// <item>The conversation flows naturally without jarring interruptions during transitions</item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Create the session activator with workflow factory
/// var activator = new WorkflowIntegratedSessionActivator(
///     workflowCoordinatorFactory,
///     realtimeAgentFactory,
///     workflowDefinition);
///
/// // Use it with the Contact Center Hub
/// var session = activator.Create(sessionId, sessionScope, loggerFactory);
/// </code>
/// </example>
public sealed class WorkflowIntegratedSessionActivator : IContactCenterConversationSessionActivator
{
    private readonly IRealtimeIvrWorkflowCoordinatorFactory _coordinatorFactory;
    private readonly Func<IServiceProvider, AuthorizingRealtimeAIAgent> _agentFactory;
    private readonly Func<string, RealtimeIvrWorkflowDefinition> _workflowFactory;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Creates a new instance of the workflow-integrated session activator.
    /// </summary>
    /// <param name="coordinatorFactory">Factory for creating workflow coordinators.</param>
    /// <param name="agentFactory">Factory for creating the realtime AI agent.</param>
    /// <param name="workflowFactory">Factory for creating the workflow definition for a session.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public WorkflowIntegratedSessionActivator(
        IRealtimeIvrWorkflowCoordinatorFactory coordinatorFactory,
        Func<IServiceProvider, AuthorizingRealtimeAIAgent> agentFactory,
        Func<string, RealtimeIvrWorkflowDefinition> workflowFactory,
        ILoggerFactory? loggerFactory = null)
    {
        _coordinatorFactory = coordinatorFactory;
        _agentFactory = agentFactory;
        _workflowFactory = workflowFactory;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public ContactCenterConversationSession Create(
        string sessionId,
        IServiceScope sessionScope,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger<WorkflowIntegratedSessionActivator>();

        // Create the realtime agent
        var agent = _agentFactory(sessionScope.ServiceProvider);

        // Create the workflow definition
        var workflowDefinition = _workflowFactory(sessionId);

        // Create the workflow coordinator
        var coordinator = _coordinatorFactory.Create(sessionId, agent, workflowDefinition);

        // Create the hub session context
        var hubSessionContext = new HubSessionContext(sessionId, sessionScope);

        // Create the conversation session
        var session = new ContactCenterConversationSession(sessionScope, hubSessionContext, loggerFactory);

        // Wire up coordinator events
        WireUpCoordinatorEvents(coordinator, session, sessionId, logger);

        // Start the workflow in the background
        _ = Task.Run(() => StartWorkflowAsync(coordinator, agent, session, sessionId, logger));

        return session;
    }

    private void WireUpCoordinatorEvents(
        RealtimeIvrWorkflowCoordinator coordinator,
        ContactCenterConversationSession session,
        string sessionId,
        ILogger logger)
    {
        // Handle step transitions
        coordinator.OnStepChanged += async (config, ct) =>
        {
            logger.LogInformation(
                "Step changed to {StepId} for session {SessionId}",
                config.StepId,
                sessionId);

            // Optionally broadcast step change to other participants (e.g., for UI updates)
            await BroadcastSystemMessageAsync(
                session,
                $"Workflow step: {config.StepId}",
                ct);
        };

        // Handle workflow completion
        coordinator.OnWorkflowCompleted += async (state, message, ct) =>
        {
            logger.LogInformation(
                "Workflow completed for session {SessionId}",
                sessionId);

            if (!string.IsNullOrEmpty(message))
            {
                await BroadcastAssistantMessageAsync(session, message, ct);
            }
        };

        // Handle workflow failure
        coordinator.OnWorkflowFailed += async (state, error, ct) =>
        {
            logger.LogWarning(
                "Workflow failed for session {SessionId}: {Error}",
                sessionId,
                error);

            await BroadcastAssistantMessageAsync(
                session,
                "I apologize, but I'm experiencing some difficulties. Let me connect you to a representative.",
                ct);
        };

        // Handle escalation
        coordinator.OnEscalationRequested += async (reason, ct) =>
        {
            logger.LogInformation(
                "Escalation requested for session {SessionId}: {Reason}",
                sessionId,
                reason);

            await BroadcastAssistantMessageAsync(
                session,
                "I'll connect you to a human representative now. Please hold.",
                ct);

            // Trigger the actual transfer
            await session.SetTransferMetadataAsync(
                new TransferMetadata
                {
                    Reason = reason,
                    Summary = $"Escalation from IVR workflow. Reason: {reason}",
                    Timestamp = DateTimeOffset.UtcNow
                },
                ct);
        };
    }

    private async Task StartWorkflowAsync(
        RealtimeIvrWorkflowCoordinator coordinator,
        AuthorizingRealtimeAIAgent agent,
        ContactCenterConversationSession session,
        string sessionId,
        ILogger logger)
    {
        try
        {
            // Wait briefly for the caller to be connected
            await Task.Delay(500);

            logger.LogInformation("Starting workflow for session {SessionId}", sessionId);

            // Initialize the coordinator and get the initial configuration
            var initialConfig = await coordinator.InitializeAsync();

            // Create the conversation thread
            var thread = coordinator.Thread
                         ?? throw new InvalidOperationException("Thread not initialized after coordinator initialization");

            // Create the workflow-aware transport
            var transport = new WorkflowAwareRealtimeAIAgentTransport(
                agent,
                thread,
                coordinator,
                loggerFactory: _loggerFactory);

            // Add the transport to the caller participant
            // The transport will run the agent stream and coordinate with the workflow
            await session.AddTransportToParticipant("caller", transport);

            // Connect the transport (this starts the agent stream)
            await transport.ConnectAsync();

            logger.LogInformation(
                "Workflow started for session {SessionId} at step {StepId}",
                sessionId,
                initialConfig.StepId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error starting workflow for session {SessionId}", sessionId);
        }
    }

    private static async Task BroadcastAssistantMessageAsync(
        ContactCenterConversationSession session,
        string text,
        CancellationToken ct)
    {
        var update = new MessageUpdate
        {
            Role = ChatRole.Assistant.ToString(),
            SenderParticipantId = "ivr-workflow",
            Contents = [new TextContent(text)]
        };

        foreach (var participant in session.ParticipantContexts.Values)
        {
            if (participant.ParticipantId != "ivr-workflow")
            {
                await participant.SendMessageAsync(update, ct);
            }
        }
    }

    private static async Task BroadcastSystemMessageAsync(
        ContactCenterConversationSession session,
        string text,
        CancellationToken ct)
    {
        var update = new MessageUpdate
        {
            Role = "system",
            SenderParticipantId = "system-events",
            Contents = [new TextContent(text)]
        };

        foreach (var participant in session.ParticipantContexts.Values)
        {
            await participant.SendMessageAsync(update, ct);
        }
    }
}
