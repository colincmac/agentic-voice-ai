using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Agents.AI.RealtimeVoice.Azure.Monitoring;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

/// <summary>
/// Handles mid-call transport failures by creating a replacement transport at a lower tier
/// and swapping it into the active session. Preserves <see cref="IvrWorkflowState"/> so the
/// caller's progress is not lost.
/// </summary>
/// <remarks>
/// Integrates with the existing <see cref="ContactCenterConversationSession.AddTransportToParticipantAsync"/>
/// and <see cref="ContactCenterConversationSession.RemoveTransportFromParticipantAsync"/> APIs.
/// The caller's ACS WebSocket transport is unaffected — only the AI agent transport is swapped.
/// </remarks>
public sealed class FallbackOrchestrator
{
    private readonly IAgentTierResolver _tierResolver;
    private readonly IReadOnlyDictionary<AgentTier, IAgentTransportFactory> _factories;
    private readonly SessionTelemetry _telemetry;
    private readonly ILogger<FallbackOrchestrator> _logger;

    public FallbackOrchestrator(
        IAgentTierResolver tierResolver,
        IEnumerable<IAgentTransportFactory> factories,
        SessionTelemetry telemetry,
        ILoggerFactory? loggerFactory = null)
    {
        _tierResolver = tierResolver;
        _factories = factories.ToDictionary(f => f.Tier, f => f);
        _telemetry = telemetry;
        _logger = loggerFactory?.CreateLogger<FallbackOrchestrator>()
                  ?? NullLogger<FallbackOrchestrator>.Instance;
    }

    /// <summary>
    /// Handles a transport failure by resolving a fallback tier, creating a replacement transport,
    /// and swapping it into the session.
    /// </summary>
    /// <param name="session">The active session.</param>
    /// <param name="participantId">The participant ID that owns the failed transport.</param>
    /// <param name="failedChannelId">The channel ID of the failed transport.</param>
    /// <param name="failedTier">The tier of the transport that failed.</param>
    /// <param name="previousWorkflowState">Workflow state from the failed transport, if available.</param>
    /// <param name="workflow">The IVR workflow definition.</param>
    public async Task HandleTransportFailureAsync(
        ContactCenterConversationSession session,
        string participantId,
        string failedChannelId,
        AgentTier failedTier,
        IvrWorkflowState? previousWorkflowState,
        RealtimeIvrWorkflowDefinition workflow)
    {
        _logger.LogWarning(
            "Transport failure detected for session {SessionId}, participant {ParticipantId}, tier {Tier}. Initiating fallback.",
            session.SessionId,
            participantId,
            failedTier);

        // 1. Resolve the next available fallback tier
        var fallbackTier = await _tierResolver.ResolveFallbackAsync(failedTier).ConfigureAwait(false);
        if (fallbackTier is null)
        {
            _logger.LogError(
                "No fallback tier available below {FailedTier} for session {SessionId}. Session will continue without AI agent.",
                failedTier,
                session.SessionId);

            return;
        }

        _logger.LogInformation(
            "Falling back from {FailedTier} to {FallbackTier} for session {SessionId}",
            failedTier,
            fallbackTier.Value,
            session.SessionId);

        // 2. Remove the failed transport
        await session.RemoveTransportFromParticipantAsync(participantId, failedChannelId).ConfigureAwait(false);

        // 3. Create replacement transport
        if (!_factories.TryGetValue(fallbackTier.Value, out var factory))
        {
            _logger.LogError("No factory registered for fallback tier {Tier}", fallbackTier.Value);

            return;
        }

        AgentTransportResult result;

        try
        {
            var sessionServices = session.HubSessionContext.SessionServices;
            result = await factory.CreateAsync(session.SessionId, sessionServices, workflow).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create fallback transport at tier {Tier} for session {SessionId}", fallbackTier.Value, session.SessionId);

            return;
        }

        // 4. Restore workflow state if available
        if (previousWorkflowState is not null && result.WorkflowState is not null)
        {
            RestoreWorkflowState(previousWorkflowState, result.WorkflowState);
        }

        // 5. Add the replacement transport
        _tierResolver.Acquire(fallbackTier.Value);
        await session.AddTransportToParticipantAsync(participantId, result.Transport).ConfigureAwait(false);

        // 6. Send a transition message to the caller
        var transitionMessage = new MessageUpdate
        {
            Contents = [new TextContent("One moment while I reconnect. I'll continue assisting you shortly.")],
            Role = "assistant",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var participant = session.ParticipantContexts.GetValueOrDefault(participantId);
        if (participant is not null)
        {
            try
            {
                await participant.SendMessageAsync(transitionMessage, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send transition message during fallback for session {SessionId}", session.SessionId);
            }
        }

        // 7. Record telemetry
        _telemetry.RecordMidCallFallback(session.SessionId, failedTier, fallbackTier.Value);

        // 8. Wire disconnect handler for cascading fallback
        result.Transport.SetOnDisconnected(async channelId =>
        {
            _tierResolver.Release(fallbackTier.Value);
            await HandleTransportFailureAsync(
                session,
                participantId,
                channelId,
                fallbackTier.Value,
                result.WorkflowState,
                workflow).ConfigureAwait(false);
        });

        _logger.LogInformation(
            "Mid-call fallback complete: {FailedTier} → {FallbackTier} for session {SessionId}",
            failedTier,
            fallbackTier.Value,
            session.SessionId);
    }

    /// <summary>
    /// Copies collected state data from the previous workflow state to the new one,
    /// preserving the caller's progress through the IVR flow.
    /// </summary>
    private static void RestoreWorkflowState(IvrWorkflowState source, IvrWorkflowState target)
    {
        target.CurrentStepName = source.CurrentStepName;
        target.Status = source.Status;
        target.TotalTurns = source.TotalTurns;

        // Copy completed steps
        foreach (var stepId in source.CompletedSteps)
        {
            target.MarkStepCompleted(stepId);
        }

        // Copy collected data
        foreach (var key in source.Keys)
        {
            target.Set(key, source.Get<object>(key));
        }
    }
}
