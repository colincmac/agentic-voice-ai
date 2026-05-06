using System.Collections.Concurrent;
using System.Threading.Channels;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Implementation;

/// <summary>
/// Default <see cref="ICallSession"/> implementation. Owns the call-scoped DI scope
/// and is responsible for:
/// <list type="bullet">
///   <item>connecting the caller edge,</item>
///   <item>starting the conversation strategy,</item>
///   <item>pumping <c>strategy.OutboundAudio → edge.SendAudioAsync</c>,</item>
///   <item>fanning <c>strategy.Events</c> out to observers,</item>
///   <item>tearing everything down on caller hangup or end.</item>
/// </list>
/// </summary>
public sealed class CallSession : ICallSession
{
    private readonly IServiceScope _scope;
    private readonly ICallSessionRegistry _registry;
    private readonly ICallQualityReporter _quality;
    private readonly ILogger<CallSession> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ICallObserver> _observers;
    private readonly List<Channel<StrategyEvent>> _observerFanout = [];
    private readonly Lock _stateLock = new();

    private IConversationStrategy _strategy;
#pragma warning disable CS0649 // assigned by AttachSupervisorAsync (not yet implemented in this slice)
    private ICallEdge? _supervisorEdge;
    private SupervisorMode? _supervisorMode;
#pragma warning restore CS0649
    private Task? _audioPump;
    private Task? _eventPump;
    private CallSessionState _state = CallSessionState.Created;
    private int _disposed;

    public CallSession(
        string callId,
        ICallEdge callerEdge,
        IConversationStrategy strategy,
        IEnumerable<ICallObserver> observers,
        ICallQualityReporter qualityReporter,
        IServiceScope scope,
        ICallSessionRegistry registry,
        ILogger<CallSession>? logger = null)
    {
        CallId = callId;
        CallerEdge = callerEdge;
        _strategy = strategy;
        _observers = [.. observers];
        _quality = qualityReporter;
        _scope = scope;
        _registry = registry;
        _logger = logger ?? NullLogger<CallSession>.Instance;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public string CallId { get; }

    public CallSessionState State => _state;

    public DateTimeOffset StartedAt { get; }

    public ICallEdge CallerEdge { get; }

    public IConversationStrategy Strategy => _strategy;

    public ICallEdge? SupervisorEdge => _supervisorEdge;

    public SupervisorMode? SupervisorMode => _supervisorMode;

    public IReadOnlyList<ICallObserver> Observers => _observers;

    public event Func<CallSessionState, ValueTask>? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await TransitionAsync(CallSessionState.Connecting).ConfigureAwait(false);

        // Seed the dashboard so updates have something to mutate.
        if (_quality is InMemoryCallQualityReporter inMem)
        {
            inMem.Register(new CallQualitySnapshot
            {
                CallId = CallId,
                State = CallSessionState.Connecting,
                ActiveTier = _strategy.Tier,
                StrategyKind = _strategy.Kind,
                StartedAt = StartedAt,
                UpdatedAt = StartedAt
            });
        }

        CallerEdge.Disconnected += OnEdgeDisconnectedAsync;
        await CallerEdge.ConnectAsync(cancellationToken).ConfigureAwait(false);

        await _strategy.StartAsync(BuildStartContext(), cancellationToken).ConfigureAwait(false);

        // Wire observers BEFORE starting the event pump so no event is dropped.
        foreach (var observer in _observers)
        {
            var bridge = Channel.CreateUnbounded<StrategyEvent>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            _observerFanout.Add(bridge);

            await observer.StartAsync(new CallObservation
            {
                CallId = CallId,
                Events = bridge.Reader,
                QualityReporter = _quality,
                Services = _scope.ServiceProvider
            }, cancellationToken).ConfigureAwait(false);
        }

        _audioPump = Task.Run(PumpAudioAsync, CancellationToken.None);
        _eventPump = Task.Run(PumpEventsAsync, CancellationToken.None);

        await TransitionAsync(CallSessionState.Active).ConfigureAwait(false);
    }

    public Task<bool> AttachSupervisorAsync(ICallEdge supervisorEdge, SupervisorMode mode, CancellationToken cancellationToken = default)
    {
        // Out of scope for the DTMF slice. Stub returns false to make the gap explicit.
        _logger.LogInformation("Supervisor attach requested for call {CallId}; not yet implemented", CallId);
        return Task.FromResult(false);
    }

    public Task<bool> ChangeSupervisorModeAsync(SupervisorMode mode, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task DetachSupervisorAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<bool> ReplaceStrategyAsync(IConversationStrategy newStrategy, CancellationToken cancellationToken = default)
    {
        await TransitionAsync(CallSessionState.Suspended).ConfigureAwait(false);

        var old = Interlocked.Exchange(ref _strategy, newStrategy);
        try
        {
            await old.StopAsync(cancellationToken).ConfigureAwait(false);
            await old.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Old strategy disposal failed"); }

        await newStrategy.StartAsync(BuildStartContext(), cancellationToken).ConfigureAwait(false);
        await TransitionAsync(CallSessionState.Active).ConfigureAwait(false);
        return true;
    }

    public Task TransferAsync(TransferRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Transfer requested for call {CallId}; not yet implemented", CallId);
        return Task.CompletedTask;
    }

    public async Task EndAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        if (_state is CallSessionState.Ended or CallSessionState.Ending)
        {
            return;
        }

        await TransitionAsync(CallSessionState.Ending).ConfigureAwait(false);

        await _strategy.StopAsync(cancellationToken).ConfigureAwait(false);
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_audioPump is not null)
        {
            try { await _audioPump.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        if (_eventPump is not null)
        {
            try { await _eventPump.ConfigureAwait(false); } catch { /* shutdown */ }
        }

        foreach (var observer in _observers)
        {
            try { await observer.StopAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Observer stop failed"); }
        }

        await TransitionAsync(CallSessionState.Ended).ConfigureAwait(false);
        await _registry.RemoveAsync(CallId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try { await EndAsync(reason: "disposed").ConfigureAwait(false); } catch { /* shutdown */ }

        try { await _strategy.DisposeAsync().ConfigureAwait(false); } catch { /* shutdown */ }
        try { await CallerEdge.DisposeAsync().ConfigureAwait(false); } catch { /* shutdown */ }

        foreach (var observer in _observers)
        {
            try { await observer.DisposeAsync().ConfigureAwait(false); } catch { /* shutdown */ }
        }

        if (_quality is InMemoryCallQualityReporter inMem)
        {
            inMem.Unregister(CallId);
        }

        _scope.Dispose();
        _cts.Dispose();
    }

    private StrategyStartContext BuildStartContext() => new()
    {
        CallId = CallId,
        InboundAudio = CallerEdge.InboundAudio,
        InboundDtmf = CallerEdge.InboundDtmf,
        Services = _scope.ServiceProvider,
        RestoreFrom = null
    };

    private async Task PumpAudioAsync()
    {
        try
        {
            await foreach (var frame in _strategy.OutboundAudio.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (!CallerEdge.IsConnected)
                {
                    break;
                }
                await CallerEdge.SendAudioAsync(frame, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio pump terminated for call {CallId}", CallId);
        }
    }

    private async Task PumpEventsAsync()
    {
        try
        {
            await foreach (var ev in _strategy.Events.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                foreach (var bridge in _observerFanout)
                {
                    bridge.Writer.TryWrite(ev);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Event pump terminated for call {CallId}", CallId);
        }
        finally
        {
            foreach (var bridge in _observerFanout)
            {
                bridge.Writer.TryComplete();
            }
        }
    }

    private async ValueTask OnEdgeDisconnectedAsync(EdgeDisconnectedReason reason)
    {
        _logger.LogInformation("Caller edge disconnected ({Reason}); ending call {CallId}", reason, CallId);
        try { await EndAsync(reason.ToString()).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "End-on-disconnect failed for call {CallId}", CallId); }
    }

    private async ValueTask TransitionAsync(CallSessionState target)
    {
        bool changed;
        lock (_stateLock)
        {
            changed = _state != target;
            _state = target;
        }

        if (!changed)
        {
            return;
        }

        _quality.Update(CallId, b => b.State = target);

        if (StateChanged is null)
        {
            return;
        }

        foreach (var handler in StateChanged.GetInvocationList().Cast<Func<CallSessionState, ValueTask>>())
        {
            try { await handler(target).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "StateChanged handler threw"); }
        }
    }
}

/// <summary>
/// Default in-process registry of active call sessions.
/// </summary>
public sealed class CallSessionRegistry : ICallSessionRegistry
{
    private readonly ConcurrentDictionary<string, ICallSession> _sessions = new();

    internal void Add(ICallSession session) => _sessions[session.CallId] = session;

    public ICallSession? TryGet(string callId)
    {
        _sessions.TryGetValue(callId, out var session);
        return session;
    }

    public IReadOnlyCollection<ICallSession> ActiveSessions => _sessions.Values.ToArray();

    public Task<bool> RemoveAsync(string callId, CancellationToken cancellationToken = default)
        => Task.FromResult(_sessions.TryRemove(callId, out _));
}

/// <summary>
/// Default <see cref="ICallSessionFactory"/>. Resolves the strategy synchronously
/// (no fire-and-forget background attach), then constructs the session.
/// </summary>
public sealed class CallSessionFactory : ICallSessionFactory
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyDictionary<AgentTier, IConversationStrategyFactory> _strategyFactories;
    private readonly CallSessionRegistry _registry;
    private readonly ICallQualityReporter _quality;
    private readonly IEnumerable<ICallObserver> _defaultObservers;
    private readonly ILoggerFactory? _loggerFactory;

    public CallSessionFactory(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IConversationStrategyFactory> strategyFactories,
        ICallSessionRegistry registry,
        ICallQualityReporter quality,
        IEnumerable<ICallObserver>? defaultObservers = null,
        ILoggerFactory? loggerFactory = null)
    {
        _scopeFactory = scopeFactory;
        _strategyFactories = strategyFactories.ToDictionary(f => f.Tier, f => f);
        _registry = (CallSessionRegistry)registry;
        _quality = quality;
        _defaultObservers = defaultObservers ?? [];
        _loggerFactory = loggerFactory;
    }

    public async Task<ICallSession> CreateAsync(CallSessionRequest request, CancellationToken cancellationToken = default)
    {
        var tier = request.PreferredTier ?? AgentTier.DtmfOnly;
        if (!_strategyFactories.TryGetValue(tier, out var factory))
        {
            throw new InvalidOperationException(
                $"No IConversationStrategyFactory registered for tier {tier}");
        }

        var scope = _scopeFactory.CreateScope();
        var strategy = await factory.CreateAsync(
            request.CallId,
            scope.ServiceProvider,
            request.Workflow,
            restoreFrom: null,
            cancellationToken).ConfigureAwait(false);

        var observers = (request.Observers ?? []).Concat(_defaultObservers).ToList();

        var session = new CallSession(
            request.CallId,
            request.CallerEdge,
            strategy,
            observers,
            _quality,
            scope,
            _registry,
            _loggerFactory?.CreateLogger<CallSession>());

        _registry.Add(session);
        return session;
    }
}
