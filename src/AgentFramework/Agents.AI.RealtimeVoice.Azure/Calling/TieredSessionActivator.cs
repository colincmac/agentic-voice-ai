using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.RealtimeVoice.Azure.Calling.Routing;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Agents.AI.RealtimeVoice.Azure.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

/// <summary>
/// Session activator that selects the best available agent tier based on current
/// capacity and creates the session with the appropriate transport.
/// <para>
/// Replaces <see cref="DefaultContactCenterConversationSessionActivator"/> when
/// tiered degradation is enabled via <see cref="ConversationHubBuilder.WithTieredDegradation"/>.
/// </para>
/// </summary>
public sealed class TieredSessionActivator : IContactCenterConversationSessionActivator
{
    private readonly IAgentTierResolver _tierResolver;
    private readonly IReadOnlyDictionary<AgentTier, IAgentTransportFactory> _factories;
    private readonly ISessionRouter _router;
    private readonly SessionTelemetry _telemetry;
    private readonly FallbackOrchestrator? _fallbackOrchestrator;
    private readonly IOptionsMonitor<AgentTierOptions> _tierOptions;
    private readonly RealtimeIvrWorkflowDefinition _workflow;
    private readonly ILogger<TieredSessionActivator> _logger;

    public TieredSessionActivator(
        IAgentTierResolver tierResolver,
        IEnumerable<IAgentTransportFactory> factories,
        ISessionRouter router,
        SessionTelemetry telemetry,
        IOptionsMonitor<AgentTierOptions> tierOptions,
        RealtimeIvrWorkflowDefinition workflow,
        FallbackOrchestrator? fallbackOrchestrator = null,
        ILoggerFactory? loggerFactory = null)
    {
        _tierResolver = tierResolver;
        _factories = factories.ToDictionary(f => f.Tier, f => f);
        _router = router;
        _telemetry = telemetry;
        _tierOptions = tierOptions;
        _workflow = workflow;
        _fallbackOrchestrator = fallbackOrchestrator;
        _logger = loggerFactory?.CreateLogger<TieredSessionActivator>()
                  ?? NullLogger<TieredSessionActivator>.Instance;
    }

    public ContactCenterConversationSession Create(
        string sessionId,
        IServiceScope sessionScope,
        ILoggerFactory loggerFactory)
    {
        var hubSessionContext = new HubSessionContext(sessionId, sessionScope);
        var session = new ContactCenterConversationSession(sessionScope, hubSessionContext, _router, _telemetry, loggerFactory);

        // Resolve tier and attach transport in the background to avoid blocking session creation
        _ = AttachTieredTransportAsync(session, sessionId, sessionScope.ServiceProvider, loggerFactory);

        return session;
    }

    private async Task AttachTieredTransportAsync(
        ContactCenterConversationSession session,
        string sessionId,
        IServiceProvider sessionServices,
        ILoggerFactory loggerFactory)
    {
        AgentTier resolvedTier;

        try
        {
            resolvedTier = await _tierResolver.ResolveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve agent tier for session {SessionId}. Falling back to {Tier}", sessionId, AgentTier.DtmfOnly);
            resolvedTier = AgentTier.DtmfOnly;
        }

        _logger.LogInformation("Session {SessionId} assigned to tier {Tier}", sessionId, resolvedTier);

        AgentTransportResult? result = null;

        // Try the resolved tier, then fall through the list if creation fails
        var options = _tierOptions.CurrentValue;
        var startIndex = options.FallbackOrder.IndexOf(resolvedTier);

        for (var i = Math.Max(0, startIndex); i < options.FallbackOrder.Count; i++)
        {
            var tier = options.FallbackOrder[i];

            if (!_factories.TryGetValue(tier, out var factory))
            {
                _logger.LogDebug("No factory registered for tier {Tier}, skipping", tier);

                continue;
            }

            try
            {
                result = await factory.CreateAsync(sessionId, sessionServices, _workflow).ConfigureAwait(false);
                resolvedTier = tier;

                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create transport for tier {Tier} in session {SessionId}, trying next tier", tier, sessionId);
            }
        }

        if (result is null)
        {
            _logger.LogError("All transport factories failed for session {SessionId}. Session will have no AI agent.", sessionId);

            return;
        }

        _tierResolver.Acquire(resolvedTier);
        _telemetry.RecordSessionCreatedAtTier(sessionId, resolvedTier);

        // Store the tier in the session context for diagnostics
        session.HubSessionContext.ConversationContext.ActionsTaken.Add($"Assigned to tier: {resolvedTier}");

        var participantId = $"agent-{resolvedTier}";
        await session.AddTransportToParticipantAsync(participantId, result.Transport).ConfigureAwait(false);

        // Wire up mid-call fallback if enabled
        if (_fallbackOrchestrator is not null && _tierOptions.CurrentValue.AllowMidCallDegradation)
        {
            result.Transport.SetOnDisconnected(async channelId =>
            {
                _tierResolver.Release(resolvedTier);

                try
                {
                    await _fallbackOrchestrator.HandleTransportFailureAsync(
                        session,
                        participantId,
                        channelId,
                        resolvedTier,
                        result.WorkflowState,
                        _workflow).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Mid-call fallback failed for session {SessionId}", sessionId);
                }
            });
        }
        else
        {
            result.Transport.SetOnDisconnected(_ =>
            {
                _tierResolver.Release(resolvedTier);

                return Task.CompletedTask;
            });
        }
    }
}
