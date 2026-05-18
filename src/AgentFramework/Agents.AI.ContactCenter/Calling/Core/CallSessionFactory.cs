using Agents.AI.ContactCenter.Telemetry;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using Agents.AI.ContactCenter.Exceptions;

namespace Agents.AI.ContactCenter.Calling.Core;

/// <summary>
/// Default <see cref="ICallSessionFactory"/>. Resolves the strategy synchronously
/// (no fire-and-forget background attach), then constructs the session.
/// </summary>
public sealed class CallSessionFactory : ICallSessionFactory
{
    /// <summary>How long a prewarmed entry survives before being evicted if no
    /// <see cref="CreateAsync"/> claims it (e.g. caller hung up before WSS).</summary>
    private static readonly TimeSpan prewarmTtl = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyDictionary<AgentTier, IConversationStrategyFactory> _strategyFactories;
    private readonly CallSessionRegistry _registry;
    private readonly ICallQualityReporter _quality;
    private readonly CallingTelemetry _telemetry;
    private readonly IEnumerable<ICallObserver> _defaultObservers;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger _logger;
    private readonly ICallOwnershipDirectory? _ownership;
    private readonly IPodHeartbeat? _heartbeat;
    private readonly ConcurrentDictionary<string, PrewarmedEntry> _prewarmed = new();

    public CallSessionFactory(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IConversationStrategyFactory> strategyFactories,
        ICallSessionRegistry registry,
        ICallQualityReporter quality,
        IEnumerable<ICallObserver>? defaultObservers = null,
        ILoggerFactory? loggerFactory = null,
        CallingTelemetry? telemetry = null,
        ICallOwnershipDirectory? ownership = null,
        IPodHeartbeat? heartbeat = null)
    {
        _scopeFactory = scopeFactory;
        _strategyFactories = strategyFactories
            .GroupBy(f => f.Tier)
            .ToDictionary(g => g.Key, g => g.Last());
        _registry = (CallSessionRegistry)registry;
        _quality = quality;
        _telemetry = telemetry ?? CallingTelemetry.Default;
        _defaultObservers = defaultObservers ?? [];
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<CallSessionFactory>() ?? NullLogger<CallSessionFactory>.Instance;
        _ownership = ownership;
        _heartbeat = heartbeat;
    }

    public Task PrewarmAsync(CallSessionPrewarmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tier = request.PreferredTier ?? AgentTier.DtmfOnly;
        if (!_strategyFactories.TryGetValue(tier, out var factory))
        {
            throw new InvalidOperationException(
                $"No IConversationStrategyFactory registered for tier {tier}");
        }

        // If a prewarm is already in flight (or completed) for this call, no-op.
        if (_prewarmed.ContainsKey(request.CallId))
        {
            return Task.CompletedTask;
        }

        var scope = _scopeFactory.CreateScope();
        var prewarmCts = new CancellationTokenSource();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(prewarmCts.Token, cancellationToken);

        // Run on a background task so the IncomingCall webhook isn't blocked.
        var strategyTask = Task.Run(async () =>
        {
            using var span = _telemetry.StartChildActivity(
                "contact_center.call.prewarm",
                request.CallId);
            span?.SetTag(CallingActivitySource.CallTierTag, tier.ToString());

            var strategy = await factory.CreateAsync(
                request.CallId,
                scope.ServiceProvider,
                request.Workflow,
                restoreFrom: null,
                linked.Token).ConfigureAwait(false);

            try
            {
                await strategy.PrewarmAsync(scope.ServiceProvider, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CallingActivitySource.SetError(span, ex);
                await strategy.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            span?.SetTag(CallingActivitySource.CallStrategyKindTag, strategy.Kind.ToString());
            return strategy;
        }, linked.Token);

        var entry = new PrewarmedEntry(scope, strategyTask, tier, prewarmCts);
        if (!_prewarmed.TryAdd(request.CallId, entry))
        {
            // Lost a race — dispose what we just built.
            _ = entry.DisposeAsync().AsTask();
            return Task.CompletedTask;
        }

        // Schedule eviction so we don't leak open realtime connections if the call never lands.
        _ = ScheduleEvictionAsync(request.CallId, prewarmTtl);

        // Surface prewarm failures in the log; CreateAsync will fall back to a fresh build.
        _ = strategyTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.LogWarning(t.Exception?.GetBaseException(),
                    "Prewarm failed for call {CallId}; CreateAsync will build a fresh strategy",
                    request.CallId);
            }
        }, TaskScheduler.Default);

        return Task.CompletedTask;
    }

    public async Task<ICallSession> CreateAsync(CallSessionRequest request, CancellationToken cancellationToken = default)
    {
        var tier = request.PreferredTier ?? AgentTier.DtmfOnly;
        if (!_strategyFactories.TryGetValue(tier, out var factory))
        {
            throw new InvalidOperationException(
                $"No IConversationStrategyFactory registered for tier {tier}");
        }

        using var createSpan = _telemetry.StartChildActivity(
            CallingActivitySource.CreateSessionActivityName,
            request.CallId);
        createSpan?.SetTag(CallingActivitySource.CallTierTag, tier.ToString());

        IServiceScope scope;
        IConversationStrategy strategy;

        if (_prewarmed.TryRemove(request.CallId, out var prewarmed) && prewarmed.Tier == tier)
        {
            createSpan?.SetTag("call.prewarm.hit", true);
            scope = prewarmed.Scope;
            try
            {
                strategy = await prewarmed.StrategyTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Prewarm faulted — fall back to a fresh build using a new scope.
                await prewarmed.DisposeAsync().ConfigureAwait(false);
                scope = _scopeFactory.CreateScope();
                strategy = await BuildStrategyAsync(factory, request, scope, createSpan, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            if (prewarmed is not null)
            {
                // Tier mismatch — caller asked for something else than what we prewarmed.
                _logger.LogInformation(
                    "Discarding prewarmed strategy for call {CallId}: prewarmed tier {PrewarmTier} != requested tier {RequestedTier}",
                    request.CallId, prewarmed.Tier, tier);
                await prewarmed.DisposeAsync().ConfigureAwait(false);
            }

            createSpan?.SetTag("call.prewarm.hit", false);
            scope = _scopeFactory.CreateScope();
            strategy = await BuildStrategyAsync(factory, request, scope, createSpan, cancellationToken).ConfigureAwait(false);
        }

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
            _loggerFactory?.CreateLogger<CallSession>(),
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

    private static async Task<IConversationStrategy> BuildStrategyAsync(
        IConversationStrategyFactory factory,
        CallSessionRequest request,
        IServiceScope scope,
        System.Diagnostics.Activity? createSpan,
        CancellationToken cancellationToken)
    {
        try
        {
            return await factory.CreateAsync(
                request.CallId,
                scope.ServiceProvider,
                request.Workflow,
                restoreFrom: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CallingActivitySource.SetError(createSpan, ex);
            scope.Dispose();
            throw;
        }
    }

    private async Task ScheduleEvictionAsync(string callId, TimeSpan ttl)
    {
        try
        {
            await Task.Delay(ttl).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        if (_prewarmed.TryRemove(callId, out var entry))
        {
            _logger.LogInformation(
                "Evicting unclaimed prewarmed strategy for call {CallId} after {Ttl}",
                callId, ttl);
            await entry.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class PrewarmedEntry : IAsyncDisposable
    {
        public PrewarmedEntry(
            IServiceScope scope,
            Task<IConversationStrategy> strategyTask,
            AgentTier tier,
            CancellationTokenSource cts)
        {
            Scope = scope;
            StrategyTask = strategyTask;
            Tier = tier;
            Cts = cts;
        }

        public IServiceScope Scope { get; }

        public Task<IConversationStrategy> StrategyTask { get; }

        public AgentTier Tier { get; }

        public CancellationTokenSource Cts { get; }

        public async ValueTask DisposeAsync()
        {
            try { await Cts.CancelAsync().ConfigureAwait(false); } catch { /* tolerated */ }

            try
            {
                var strategy = await StrategyTask.ConfigureAwait(false);
                await strategy.DisposeAsync().ConfigureAwait(false);
            }
            catch { /* prewarm faulted or cancelled */ }

            try { Scope.Dispose(); } catch { /* tolerated */ }
            try { Cts.Dispose(); } catch { /* tolerated */ }
        }
    }
}
