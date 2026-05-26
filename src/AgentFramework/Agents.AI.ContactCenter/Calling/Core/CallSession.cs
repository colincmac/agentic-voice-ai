using System.Diagnostics;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Calling.Core;

/// <summary>
/// Default <see cref="ICallSession"/> implementation. Owns the call-scoped DI scope
/// and is responsible for:
/// <list type="bullet">
///   <item>connecting the caller edge,</item>
///   <item>starting the conversation strategy,</item>
///   <item>fanning caller audio to the strategy and (when attached) supervisor,</item>
///   <item>fanning strategy audio to the caller and (when attached) supervisor,</item>
///   <item>bridging supervisor audio to the caller during BargeIn,</item>
///   <item>fanning <c>strategy.Events</c> out to observers,</item>
///   <item>tearing everything down on caller hangup or end.</item>
/// </list>
/// </summary>
public sealed class CallSession : ICallSession
{
    private readonly IServiceScope _scope;
    private readonly ICallSessionRegistry _registry;
    private readonly ICallQualityReporter _quality;
    private readonly CallingTelemetry _telemetry;
    private readonly ILogger<CallSession> _logger;
    private readonly ICallOwnershipDirectory? _ownership;
    private readonly IPodHeartbeat? _heartbeat;
    private readonly CancellationTokenSource _cts = new();
    private Activity? _callActivity;
    private DateTimeOffset _lastStateChangeAt = DateTimeOffset.UtcNow;
    private long _firstAudioRecorded;
    private readonly List<ICallObserver> _observers;
    private readonly List<Channel<StrategyEvent>> _observerFanout = [];
    private readonly Lock _stateLock = new();

    // Caller inbound audio is teed at the session level so the supervisor can
    // listen in (Monitor) without affecting the strategy's view.
    private readonly Channel<AudioFrame> _strategyInbound = Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(500)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private IConversationStrategy _strategy;

    // Supervisor wiring. All four are mutated under _stateLock.
    private ICallEdge? _supervisorEdge;
    private SupervisorMode? _supervisorMode;
    private CancellationTokenSource? _supervisorCts;
    private Task? _supervisorPumps;

    private Task? _outboundPump;
    private Task? _inboundFanoutPump;
    private Task? _eventPump;
    private CallSessionState _state = CallSessionState.Created;
    private CallSessionState _stateBeforeSuspend = CallSessionState.Active;
    private int _disposed;

    public CallSession(
        string callId,
        ICallEdge callerEdge,
        IConversationStrategy strategy,
        IEnumerable<ICallObserver> observers,
        ICallQualityReporter qualityReporter,
        IServiceScope scope,
        ICallSessionRegistry registry,
        ILogger<CallSession> logger,
        CallingTelemetry telemetry,
        ICallOwnershipDirectory? ownership = null,
        IPodHeartbeat? heartbeat = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(telemetry);

        CallId = callId;
        CallerEdge = callerEdge;
        _strategy = strategy;
        _observers = [.. observers];
        _quality = qualityReporter;
        _scope = scope;
        _registry = registry;
        _telemetry = telemetry;
        _logger = logger;
        _ownership = ownership;
        _heartbeat = heartbeat;
        StartedAt = DateTimeOffset.UtcNow;
        _lastStateChangeAt = StartedAt;
    }

    public string CallId { get; }

    public CallSessionState State => _state;

    public DateTimeOffset StartedAt { get; }

    public ICallEdge CallerEdge { get; }

    public IConversationStrategy Strategy => _strategy;

    public ICallEdge? SupervisorEdge
    {
        get { lock (_stateLock) { return _supervisorEdge; } }
    }

    public SupervisorMode? SupervisorMode
    {
        get { lock (_stateLock) { return _supervisorMode; } }
    }

    public IReadOnlyList<ICallObserver> Observers => _observers;

    public event Func<CallSessionState, ValueTask>? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _callActivity = _telemetry.StartCallActivity(CallId, _strategy.Tier, _strategy.Kind);
        _telemetry.CallStarted(CallId, _strategy.Tier);

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

        _inboundFanoutPump = Task.Run(PumpCallerInboundAsync, CancellationToken.None);
        _outboundPump = Task.Run(PumpStrategyOutboundAsync, CancellationToken.None);
        _eventPump = Task.Run(PumpEventsAsync, CancellationToken.None);

        await TransitionAsync(CallSessionState.Active).ConfigureAwait(false);
    }

    public async Task<bool> AttachSupervisorAsync(
        ICallEdge supervisorEdge,
        SupervisorMode mode,
        CancellationToken cancellationToken = default)
    {
        if (_state is CallSessionState.Ended or CallSessionState.Ending or CallSessionState.Faulted)
        {
            return false;
        }

        ICallEdge? existing;
        lock (_stateLock) { existing = _supervisorEdge; }
        if (existing is not null)
        {
            _logger.LogWarning("Supervisor already attached to call {CallId}; detach first", CallId);
            return false;
        }

        await supervisorEdge.ConnectAsync(cancellationToken).ConfigureAwait(false);
        supervisorEdge.Disconnected += OnSupervisorDisconnectedAsync;

        var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var pump = Task.Run(() => PumpSupervisorInboundAsync(supervisorEdge, pumpCts.Token), CancellationToken.None);

        lock (_stateLock)
        {
            _supervisorEdge = supervisorEdge;
            _supervisorMode = mode;
            _supervisorCts = pumpCts;
            _supervisorPumps = pump;
        }

        _quality.Update(CallId, current => current with
        {
            Supervisor = new SupervisorPresence(
                SupervisorId: supervisorEdge.EdgeId,
                DisplayName: supervisorEdge.Metadata.DisplayName,
                Mode: mode,
                AttachedAt: DateTimeOffset.UtcNow)
        });
        _quality.RaiseAlert(CallId, new QualityAlert(
            AlertId: $"supervisor-{supervisorEdge.EdgeId}",
            Kind: QualityAlertKind.SupervisorWhisper,
            Severity: QualityAlertSeverity.Info,
            Message: $"Supervisor attached in {mode} mode",
            RaisedAt: DateTimeOffset.UtcNow));

        await ApplyModeAsync(mode, cancellationToken).ConfigureAwait(false);
        _telemetry.SupervisorAttached(CallId, mode);
        using (var attachSpan = _telemetry.StartChildActivity(CallingActivitySource.SupervisorAttachActivityName, CallId))
        {
            attachSpan?.SetTag(CallingActivitySource.SupervisorIdTag, supervisorEdge.EdgeId);
            attachSpan?.SetTag(CallingActivitySource.SupervisorModeTag, mode.ToString());
        }
        _logger.LogInformation("Supervisor {SupervisorId} attached to call {CallId} in {Mode} mode",
            supervisorEdge.EdgeId, CallId, mode);
        return true;
    }

    public async Task<bool> ChangeSupervisorModeAsync(SupervisorMode mode, CancellationToken cancellationToken = default)
    {
        SupervisorMode? current;
        lock (_stateLock)
        {
            if (_supervisorEdge is null)
            {
                return false;
            }
            current = _supervisorMode;
            _supervisorMode = mode;
        }

        if (current == mode)
        {
            return true;
        }

        _quality.Update(CallId, current => current.Supervisor is null
            ? current
            : current with { Supervisor = current.Supervisor with { Mode = mode } });

        await ApplyModeAsync(mode, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Supervisor mode for call {CallId} changed: {From} → {To}", CallId, current, mode);
        return true;
    }

    public async Task DetachSupervisorAsync(CancellationToken cancellationToken = default)
    {
        ICallEdge? supervisor;
        SupervisorMode? lastMode;
        CancellationTokenSource? pumpCts;
        Task? pumps;
        lock (_stateLock)
        {
            supervisor = _supervisorEdge;
            lastMode = _supervisorMode;
            pumpCts = _supervisorCts;
            pumps = _supervisorPumps;
            _supervisorEdge = null;
            _supervisorMode = null;
            _supervisorCts = null;
            _supervisorPumps = null;
        }

        if (supervisor is null)
        {
            return;
        }

        supervisor.Disconnected -= OnSupervisorDisconnectedAsync;

        if (pumpCts is not null)
        {
            try { await pumpCts.CancelAsync().ConfigureAwait(false); } catch { /* tolerated */ }
            pumpCts.Dispose();
        }
        if (pumps is not null)
        {
            try { await pumps.ConfigureAwait(false); } catch { /* shutdown */ }
        }

        // If we were in BargeIn, lift the suspend now that the supervisor is gone.
        // Skip the resume / state revert when the call is already winding down — the
        // strategy is about to be stopped anyway and Ending → Active would be wrong.
        var endingNow = _state is CallSessionState.Ending or CallSessionState.Ended;
        if (lastMode is Calling.SupervisorMode.BargeIn && !endingNow)
        {
            try { await _strategy.ResumeAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Strategy resume on detach failed"); }

            await TransitionAsync(_stateBeforeSuspend).ConfigureAwait(false);
        }

        _quality.Update(CallId, current => current with { Supervisor = null });
        _quality.ResolveAlert(CallId, $"supervisor-{supervisor.EdgeId}");

        try { await supervisor.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Supervisor edge dispose failed"); }

        _telemetry.SupervisorDetached(CallId, lastMode);
        _logger.LogInformation("Supervisor detached from call {CallId}", CallId);
    }

    public async Task<bool> ReplaceStrategyAsync(IConversationStrategy newStrategy, CancellationToken cancellationToken = default)
    {
        using var span = _telemetry.StartChildActivity(CallingActivitySource.ReplaceStrategyActivityName, CallId);
        span?.SetTag("strategy.from.kind", _strategy.Kind.ToString());
        span?.SetTag("strategy.to.kind", newStrategy.Kind.ToString());

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

    public async Task TransferAsync(TransferRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_state is CallSessionState.Ended or CallSessionState.Ending or CallSessionState.Faulted)
        {
            _logger.LogWarning("Transfer requested for call {CallId} in terminal state {State}; ignoring", CallId, _state);
            return;
        }

        if (CallerEdge is not ICallControl control || !control.CanControl)
        {
            throw new InvalidOperationException(
                $"Call {CallId} cannot be transferred: caller edge {CallerEdge.GetType().Name} does not support call control.");
        }

        using var span = _telemetry.StartChildActivity(CallingActivitySource.TransferActivityName, CallId);
        span?.SetTag(CallingActivitySource.TransferKindTag, request.Kind.ToString());
        span?.SetTag(CallingActivitySource.TransferTargetTag, request.TargetIdentifier);
        _telemetry.TransferInitiated(CallId, request.Kind);

        _logger.LogInformation(
            "Transferring call {CallId} to {Target} ({Kind})",
            CallId, request.TargetIdentifier, request.Kind);

        // Stop the strategy first so it doesn't keep speaking over the transfer.
        try { await _strategy.StopAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex)
        {
            CallingActivitySource.SetError(span, ex);
            _logger.LogWarning(ex, "Strategy stop on transfer failed for call {CallId}", CallId);
        }

        try
        {
            await control.TransferAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CallingActivitySource.SetError(span, ex);
            throw;
        }

        _quality.RaiseAlert(CallId, new QualityAlert(
            AlertId: $"transfer-{Guid.NewGuid():N}",
            Kind: QualityAlertKind.SupervisorWhisper,
            Severity: QualityAlertSeverity.Info,
            Message: $"Transfer initiated to {request.TargetIdentifier} ({request.Kind})",
            RaisedAt: DateTimeOffset.UtcNow));
    }

    public async Task HangUpAsync(bool hangUpForEveryone = true, string? reason = null, CancellationToken cancellationToken = default)
    {
        if (_state is CallSessionState.Ended or CallSessionState.Ending)
        {
            return;
        }

        using var span = _telemetry.StartChildActivity(CallingActivitySource.HangupActivityName, CallId);
        span?.SetTag(CallingActivitySource.HangupForEveryoneTag, hangUpForEveryone);
        span?.SetTag(CallingActivitySource.HangupReasonTag, reason);
        _telemetry.HangupIssued(CallId, hangUpForEveryone, reason);

        if (CallerEdge is ICallControl control && control.CanControl)
        {
            try
            {
                _logger.LogInformation(
                    "Hanging up call {CallId} (forEveryone={ForEveryone}) reason={Reason}",
                    CallId, hangUpForEveryone, reason ?? "<none>");
                await control.HangUpAsync(hangUpForEveryone, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Platform hang-up failed for call {CallId}; tearing down locally", CallId);
            }
        }
        else
        {
            _logger.LogWarning(
                "Call {CallId} caller edge {EdgeKind} does not support call control; tearing down locally only",
                CallId, CallerEdge.GetType().Name);
        }

        await EndAsync(reason ?? "hangup", cancellationToken).ConfigureAwait(false);
    }

    public async Task EndAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        if (_state is CallSessionState.Ended or CallSessionState.Ending)
        {
            return;
        }

        await TransitionAsync(CallSessionState.Ending).ConfigureAwait(false);

        await DetachSupervisorAsync(cancellationToken).ConfigureAwait(false);

        await _strategy.StopAsync(cancellationToken).ConfigureAwait(false);
        await _cts.CancelAsync().ConfigureAwait(false);
        _strategyInbound.Writer.TryComplete();

        if (_inboundFanoutPump is not null)
        {
            try { await _inboundFanoutPump.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        if (_outboundPump is not null)
        {
            try { await _outboundPump.ConfigureAwait(false); } catch { /* shutdown */ }
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

        if (_ownership is not null)
        {
            try { await _ownership.ReleaseAsync(CallId, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Ownership release failed for call {CallId}", CallId); }
        }
        _heartbeat?.UntrackOwnedCall(CallId);

        var duration = DateTimeOffset.UtcNow - StartedAt;
        _telemetry.CallEnded(CallId, _strategy.Tier, _state, reason, duration);
        if (_callActivity is not null)
        {
            _callActivity.SetTag(CallingActivitySource.CallEndReasonTag, reason ?? "unspecified");
            _callActivity.SetTag("call.duration_s", duration.TotalSeconds);
            _callActivity.Stop();
            _callActivity.Dispose();
            _callActivity = null;
        }
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
        InboundAudio = _strategyInbound.Reader,
        InboundDtmf = CallerEdge.InboundDtmf,
        Services = _scope.ServiceProvider,
        CallerMetadata = CallerEdge.Metadata,
        RestoreFrom = null
    };

    /// <summary>
    /// Reads caller audio off the edge and fans it to the strategy. When a
    /// supervisor is attached in Monitor or Whisper mode, the supervisor also
    /// hears the caller. In BargeIn mode the strategy stops receiving caller
    /// audio (the supervisor is talking instead).
    /// </summary>
    private async Task PumpCallerInboundAsync()
    {
        try
        {
            await foreach (var frame in CallerEdge.InboundAudio.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                _telemetry.InboundAudioFrame(CallerEdge.EdgeId);
                ICallEdge? supervisor;
                SupervisorMode? mode;
                lock (_stateLock)
                {
                    supervisor = _supervisorEdge;
                    mode = _supervisorMode;
                }

                if (mode is not Calling.SupervisorMode.BargeIn)
                {
                    await _strategyInbound.Writer.WriteAsync(frame, _cts.Token).ConfigureAwait(false);
                }

                if (supervisor is not null && mode is Calling.SupervisorMode.Monitor or Calling.SupervisorMode.Whisper
                    && supervisor.Capabilities.HasFlag(EdgeCapabilities.Audio))
                {
                    try { await supervisor.DispatchAsync(new OutboundDirective.Audio(frame), _cts.Token).ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Supervisor caller-tap send failed"); }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex) { _logger.LogWarning(ex, "Caller inbound pump terminated for call {CallId}", CallId); }
        finally
        {
            _strategyInbound.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Reads strategy outbound directives and dispatches them to the caller. In
    /// Monitor mode the supervisor receives a tap of any Audio directive (other
    /// directive kinds aren't audible — supervisors are streaming edges). In BargeIn
    /// the strategy is suspended and no directive reaches the caller.
    /// </summary>
    private async Task PumpStrategyOutboundAsync()
    {
        try
        {
            await foreach (var directive in _strategy.Outbound.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                ICallEdge? supervisor;
                SupervisorMode? mode;
                lock (_stateLock)
                {
                    supervisor = _supervisorEdge;
                    mode = _supervisorMode;
                }

                // BargeIn keeps strategy output off the caller's wire even if the
                // strategy hasn't drained yet from its suspend signal.
                if (mode is not Calling.SupervisorMode.BargeIn && CallerEdge.IsConnected)
                {
                    if (CallerEdge.Capabilities.HasFlag(DirectiveCapability(directive)))
                    {
                        var dispatchStart = Stopwatch.GetTimestamp();
                        await CallerEdge.DispatchAsync(directive, _cts.Token).ConfigureAwait(false);
                        _telemetry.DirectiveDispatched(
                            CallerEdge.EdgeId,
                            directive.GetType().Name,
                            Stopwatch.GetElapsedTime(dispatchStart));

                        if (Interlocked.Exchange(ref _firstAudioRecorded, 1) == 0)
                        {
                            _telemetry.RecordTimeToFirstAudio(CallId, _strategy.Tier, DateTimeOffset.UtcNow - StartedAt);
                        }
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Caller edge for call {CallId} cannot dispatch {DirectiveKind}; capabilities are {Capabilities}",
                            CallId, directive.GetType().Name, CallerEdge.Capabilities);
                        _telemetry.DirectiveUnsupported(CallerEdge.EdgeId, directive.GetType().Name, CallerEdge.Capabilities);
                        // Surface the mismatch so observers / dashboards can flag it.
                        var mismatch = new StrategyEvent.DispatchUnsupported(
                            directive.GetType().Name,
                            CallerEdge.Capabilities,
                            DateTimeOffset.UtcNow);
                        foreach (var bridge in _observerFanout)
                        {
                            bridge.Writer.TryWrite(mismatch);
                        }
                    }
                }

                if (supervisor is not null
                    && mode is Calling.SupervisorMode.Monitor
                    && directive is OutboundDirective.Audio agentAudio
                    && supervisor.Capabilities.HasFlag(EdgeCapabilities.Audio))
                {
                    try { await supervisor.DispatchAsync(agentAudio, _cts.Token).ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Supervisor agent-tap send failed"); }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex) { _logger.LogWarning(ex, "Outbound pump terminated for call {CallId}", CallId); }
    }

    private static EdgeCapabilities DirectiveCapability(OutboundDirective directive) => directive switch
    {
        OutboundDirective.Audio => EdgeCapabilities.Audio,
        OutboundDirective.SpeakText => EdgeCapabilities.SpeakText,
        OutboundDirective.PlayFile => EdgeCapabilities.PlayFile,
        OutboundDirective.StopPlayback => EdgeCapabilities.StopPlayback,
        OutboundDirective.CollectDtmf => EdgeCapabilities.CollectDtmf,
        _ => EdgeCapabilities.None
    };

    /// <summary>
    /// Reads supervisor inbound audio. In BargeIn it bridges to the caller. In
    /// Whisper it forwards to the strategy via <see cref="IWhisperableStrategy"/>
    /// when supported. In Monitor (the default tap-only mode) it is dropped.
    /// </summary>
    private async Task PumpSupervisorInboundAsync(ICallEdge supervisor, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in supervisor.InboundAudio.ReadAllAsync(ct).ConfigureAwait(false))
            {
                SupervisorMode? mode;
                lock (_stateLock) { mode = _supervisorMode; }

                switch (mode)
                {
                    case Calling.SupervisorMode.BargeIn when CallerEdge.IsConnected
                                                              && CallerEdge.Capabilities.HasFlag(EdgeCapabilities.Audio):
                        try { await CallerEdge.DispatchAsync(new OutboundDirective.Audio(frame), ct).ConfigureAwait(false); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Supervisor BargeIn send failed"); }
                        break;

                    case Calling.SupervisorMode.Whisper when _strategy is IWhisperableStrategy whisperable:
                        try
                        {
                            await whisperable.InjectWhisperAsync(new SupervisorWhisper
                            {
                                SupervisorId = supervisor.EdgeId,
                                Audio = frame.Pcm,
                                At = frame.Timestamp
                            }, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) { _logger.LogDebug(ex, "Whisper inject failed"); }
                        break;

                    // Monitor / Whisper-without-support: drop.
                }
            }
        }
        catch (OperationCanceledException) { /* swap or shutdown */ }
        catch (Exception ex) { _logger.LogWarning(ex, "Supervisor inbound pump terminated"); }
    }

    /// <summary>
    /// Apply a mode transition: BargeIn suspends the strategy and saves the prior
    /// state for resume; any other mode resumes the strategy if we were suspended.
    /// </summary>
    private async Task ApplyModeAsync(SupervisorMode mode, CancellationToken ct)
    {
        if (mode is Calling.SupervisorMode.BargeIn)
        {
            if (_state is not CallSessionState.Suspended)
            {
                _stateBeforeSuspend = _state;
            }
            try { await _strategy.SuspendAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Strategy suspend failed"); }

            await TransitionAsync(CallSessionState.Suspended).ConfigureAwait(false);
        }
        else if (_state is CallSessionState.Suspended)
        {
            try { await _strategy.ResumeAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Strategy resume failed"); }

            await TransitionAsync(_stateBeforeSuspend).ConfigureAwait(false);
        }
    }

    private async Task PumpEventsAsync()
    {
        try
        {
            await foreach (var ev in _strategy.Events.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                _telemetry.StrategyEventEmitted(CallId, ev);
                using (_telemetry.StartStrategyEventActivity(CallId, ev)) { /* span captured in using to record end time */ }

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

    private async ValueTask OnSupervisorDisconnectedAsync(EdgeDisconnectedReason reason)
    {
        _logger.LogInformation("Supervisor edge disconnected ({Reason}) on call {CallId}", reason, CallId);
        try { await DetachSupervisorAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Detach-on-disconnect failed for call {CallId}", CallId); }
    }

    private async ValueTask TransitionAsync(CallSessionState target)
    {
        bool changed;
        CallSessionState previous;
        DateTimeOffset previousAt;
        lock (_stateLock)
        {
            previous = _state;
            previousAt = _lastStateChangeAt;
            changed = _state != target;
            _state = target;
            if (changed)
            {
                _lastStateChangeAt = DateTimeOffset.UtcNow;
            }
        }

        if (!changed)
        {
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - previousAt;
        _telemetry.StateTransition(CallId, previous, target, elapsed);
        _callActivity?.AddEvent(new ActivityEvent(
            name: "call.state_transition",
            tags: new ActivityTagsCollection
            {
                { CallingActivitySource.CallStateFromTag, previous.ToString() },
                { CallingActivitySource.CallStateToTag, target.ToString() },
                { "elapsed_ms", elapsed.TotalMilliseconds },
            }));
        if (target == CallSessionState.Faulted)
        {
            _telemetry.CallFaulted(CallId, _strategy.Tier, previous.ToString());
            _callActivity?.SetStatus(ActivityStatusCode.Error, "call faulted");
        }

        _quality.Update(CallId, current => current with { State = target });

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
