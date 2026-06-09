using Agents.AI.ContactCenter.Telemetry;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agents.AI.ContactCenter.Exceptions;
using System.Diagnostics;

namespace Agents.AI.ContactCenter.Calling.Core;

/// <summary>
/// Default <see cref="ICallSessionFactory"/>. Creates the per-call DI scope, binds the
/// chosen workflow, resolves the top-tier <see cref="IConversationStrategy"/> from that
/// scope via keyed DI, then constructs the session.
/// </summary>
public sealed class CallSessionFactory : ICallSessionFactory
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CallSessionRegistry _registry;
    private readonly ICallQualityReporter _quality;
    private readonly CallingTelemetry _telemetry;
    private readonly IEnumerable<ICallObserver> _defaultObservers;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly ICallOwnershipDirectory? _ownership;
    private readonly IPodHeartbeat? _heartbeat;

    public CallSessionFactory(
        IServiceScopeFactory scopeFactory,
        ICallSessionRegistry registry,
        ICallQualityReporter quality,
        ILoggerFactory loggerFactory,
        CallingTelemetry telemetry,
        IEnumerable<ICallObserver>? defaultObservers = null,
        ICallOwnershipDirectory? ownership = null,
        IPodHeartbeat? heartbeat = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(telemetry);

        _scopeFactory = scopeFactory;
        _registry = (CallSessionRegistry)registry;
        _quality = quality;
        _telemetry = telemetry;
        _defaultObservers = defaultObservers ?? [];
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CallSessionFactory>();
        _ownership = ownership;
        _heartbeat = heartbeat;
    }

    public async Task<ICallSession> CreateAsync(CallSessionRequest request, CancellationToken cancellationToken = default)
    {
        var tier = request.PreferredTier ?? AgentTier.DtmfOnly;

        using var createSpan = _telemetry.StartChildActivity(
            CallingActivitySource.CreateSessionActivityName,
            request.CallId);
        createSpan?.SetTag(CallingActivitySource.CallTierTag, tier.ToString());

        var scope = _scopeFactory.CreateScope();
        var strategy = BuildStrategy(tier, request, scope, createSpan);

        _telemetry.CallCreated(request.CallId, tier, strategy.Kind);
        createSpan?.SetTag(CallingActivitySource.CallStrategyKindTag, strategy.Kind.ToString());

        var observers = (request.Observers ?? []).Concat(_defaultObservers).ToList();

        var session = new CallSession(
            request.CallId,
            request.CallerEdge,
            strategy,
            observers,
            _quality,
            scope,
            _registry,
            _loggerFactory.CreateLogger<CallSession>(),
            _telemetry,
            _ownership,
            _heartbeat);

        // Bind the per-call scoped accessor so AI tool collections resolved
        // from this scope can reach the live session.
        var accessor = scope.ServiceProvider.GetService<CallSessionAccessor>();
        accessor?.Set(session);

        _registry.Add(session);

        if (_ownership is not null)
        {
            // Safe default: pin every call to this pod. Verb-only strategies
            // could tolerate cross-pod callbacks, but over-pinning is correct
            // (forwarding hop) while under-pinning risks dual-state.
            const CallOwnershipKind kind = CallOwnershipKind.Streaming;
            var acquire = await _ownership.TryAcquireAsync(request.CallId, kind, cancellationToken).ConfigureAwait(false);
            if (!acquire.Acquired)
            {
                _logger.LogInformation(
                    "Call {CallId} is owned by cluster={OwnerCluster} pod={OwnerPod}; refusing local create",
                    request.CallId, acquire.Owner.ClusterId, acquire.Owner.PodId);
                createSpan?.SetTag("call.ownership.acquired", false);
                await _registry.RemoveAsync(request.CallId, cancellationToken).ConfigureAwait(false);
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Session dispose after ownership conflict failed for call {CallId}", request.CallId); }
                throw new CallOwnershipConflictException(request.CallId, acquire.Owner);
            }

            createSpan?.SetTag("call.ownership.acquired", true);
            _heartbeat?.TrackOwnedCall(request.CallId, kind);
        }

        return session;
    }

    private static IConversationStrategy BuildStrategy(
        AgentTier tier,
        CallSessionRequest request,
        IServiceScope scope,
        Activity? createSpan)
    {
        try
        {
            // Bind the chosen workflow on this scope so the keyed strategy registration resolves it.
            scope.ServiceProvider.GetService<CallWorkflowSelection>()?.Set(request.WorkflowId);

            // Resolve the top-tier strategy from the scope via keyed DI. The composite (when
            // registered) shadows any single-tier registration at the same key, so this single
            // resolve returns either the leaf strategy or the composite wrapper.
            return scope.ServiceProvider.GetRequiredKeyedService<IConversationStrategy>(tier);
        }
        catch (Exception ex)
        {
            CallingActivitySource.SetError(createSpan, ex);
            scope.Dispose();
            throw new InvalidOperationException(
                $"No IConversationStrategy registered for tier {tier}. " +
                $"Register one via AddRealtimeCallWorkflowStrategy / AddNluCallWorkflowStrategy / " +
                $"AddDtmfCallWorkflowStrategy / AddCompositeFallbackStrategy.",
                ex);
        }
    }
}
