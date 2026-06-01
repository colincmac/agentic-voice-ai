using System.Threading.Channels;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Registry;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Calling.Strategies.AgentEnsemble;

/// <summary>
/// Strategy that drives an <see cref="IAgentEnsemble"/>: one primary speaker emits
/// audio + transcripts to the caller, delegate agents run in parallel and surface
/// <see cref="StrategyEvent.DelegateInsight"/> events. Handoff between speakers is
/// driven by <see cref="IAgentEnsemble.PromoteAsync"/>; the strategy re-pumps the
/// new primary's backend automatically.
/// </summary>
public sealed class AgentEnsembleStrategy : IConversationStrategy
{
    private const int RecentBufferSize = 25;

    private readonly IAgentEnsemble _ensemble;
    private readonly RealtimeIvrWorkflowDefinition _workflow;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger _logger;

    private readonly Channel<OutboundDirective> _outbound = Channel.CreateBounded<OutboundDirective>(
        new BoundedChannelOptions(500)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly Channel<StrategyEvent> _events = Channel.CreateUnbounded<StrategyEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly RecentBuffer<StrategyEvent.Transcript> _transcripts = new(RecentBufferSize);
    private readonly RecentBuffer<StrategyEvent.AgentUtterance> _utterances = new(RecentBufferSize);
    private readonly RecentBuffer<AgentInsight> _recentInsights = new(RecentBufferSize);

    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _primaryLock = new();

    private IIvrWorkflowNavigator? _navigator;
    private StrategyStartContext? _startContext;
    private CancellationTokenSource? _primaryPumpCts;
    private Task? _primaryPumps;
    private Task? _insightsLoop;
    private Task? _delegateContextLoop;
    private bool _suspended;

    public AgentEnsembleStrategy(
        IAgentEnsemble ensemble,
        RealtimeIvrWorkflowDefinition workflow,
        IvrWorkflowState? restoreFrom = null,
        ILoggerFactory? loggerFactory = null)
    {
        _ensemble = ensemble;
        _workflow = workflow;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<AgentEnsembleStrategy>() ?? NullLogger<AgentEnsembleStrategy>.Instance;

        WorkflowState = restoreFrom ?? new IvrWorkflowState { Status = IvrWorkflowStatus.Running };
    }

    public StrategyKind Kind => StrategyKind.AgentEnsemble;

    public AgentTier Tier => AgentTier.RealtimeVoice;

    public IvrWorkflowState WorkflowState { get; }

    public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;

    public EdgeCapabilities EmittedDirectives => EdgeCapabilities.Audio | EdgeCapabilities.StopPlayback;

    public ChannelReader<StrategyEvent> Events => _events.Reader;

    public async Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default)
    {
        if (_startContext is not null)
        {
            return;
        }
        _startContext = context;

        _navigator = new IvrWorkflowNavigator(
            _workflow,
            WorkflowState,
            context.Services,
            _loggerFactory?.CreateLogger<IvrWorkflowNavigator>());

        _ensemble.PrimaryChanged += OnPrimaryChangedAsync;

        var primary = _ensemble.PrimaryAgent;
        await primary.Backend.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var step = _navigator.EnterInitialStep();
        await ApplyStageOnAsync(primary, step, cancellationToken).ConfigureAwait(false);

        await EmitAsync(new StrategyEvent.AgentSpeakingChanged(primary.AgentId, primary.DisplayName, DateTimeOffset.UtcNow)).ConfigureAwait(false);

        StartPrimaryPumps(primary);

        _insightsLoop = Task.Run(RunInsightsLoopAsync, CancellationToken.None);
        _delegateContextLoop = Task.Run(RunDelegateContextLoopAsync, CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _ensemble.PrimaryChanged -= OnPrimaryChangedAsync;

        await _cts.CancelAsync().ConfigureAwait(false);

        var primaryPumps = _primaryPumps;
        if (primaryPumps is not null)
        {
            try { await primaryPumps.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        if (_insightsLoop is not null)
        {
            try { await _insightsLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        if (_delegateContextLoop is not null)
        {
            try { await _delegateContextLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        }

        _outbound.Writer.TryComplete();
        _events.Writer.TryComplete();
    }

    public ValueTask SuspendAsync(CancellationToken cancellationToken = default)
    {
        _suspended = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default)
    {
        _suspended = false;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _ensemble.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private void StartPrimaryPumps(IConversationalAgent primary)
    {
        var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var inboundPump = Task.Run(() => PumpInboundAudioAsync(primary, pumpCts.Token), CancellationToken.None);
        var backendLoop = Task.Run(() => RunBackendLoopAsync(primary, pumpCts.Token), CancellationToken.None);

        lock (_primaryLock)
        {
            _primaryPumpCts = pumpCts;
            _primaryPumps = Task.WhenAll(inboundPump, backendLoop);
        }
    }

    private async ValueTask OnPrimaryChangedAsync(IConversationalAgent newPrimary)
    {
        CancellationTokenSource? oldCts;
        Task? oldPumps;
        lock (_primaryLock)
        {
            oldCts = _primaryPumpCts;
            oldPumps = _primaryPumps;
        }

        try { await newPrimary.Backend.ConnectAsync(_cts.Token).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "New primary backend connect failed"); }

        try
        {
            if (_navigator?.CurrentStep is { } currentStep)
            {
                await ApplyStageOnAsync(newPrimary, currentStep, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "New primary prompt update failed"); }

        StartPrimaryPumps(newPrimary);

        await EmitAsync(new StrategyEvent.AgentSpeakingChanged(newPrimary.AgentId, newPrimary.DisplayName, DateTimeOffset.UtcNow))
            .ConfigureAwait(false);

        // Cancel old pumps AFTER the new ones are running so caller audio has somewhere to go.
        if (oldCts is not null)
        {
            try { await oldCts.CancelAsync().ConfigureAwait(false); } catch { /* tolerated */ }
            oldCts.Dispose();
        }
        if (oldPumps is not null)
        {
            try { await oldPumps.ConfigureAwait(false); } catch { /* tolerated */ }
        }
    }

    private async Task PumpInboundAudioAsync(IConversationalAgent primary, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _startContext!.InboundAudio.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_suspended)
                {
                    continue;
                }
                await primary.Backend.SendAudioAsync(frame.Pcm, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* primary swap or shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ensemble inbound pump terminated for primary {AgentId}", primary.AgentId);
        }
    }

    private async Task RunBackendLoopAsync(IConversationalAgent primary, CancellationToken ct)
    {
        try
        {
            await foreach (var update in primary.Backend.RunAsync(ct).ConfigureAwait(false))
            {
                switch (update)
                {
                    case RealtimeBackendUpdate.Audio audio when !_suspended:
                        await _outbound.Writer.WriteAsync(
                            new OutboundDirective.Audio(
                                new AudioFrame(audio.Pcm, audio.At, SourceEdgeId: primary.AgentId)),
                            ct).ConfigureAwait(false);
                        break;

                    case RealtimeBackendUpdate.Transcript transcript:
                        var tEvent = new StrategyEvent.Transcript(transcript.Speaker, transcript.Text, transcript.IsFinal, transcript.At);
                        if (transcript.IsFinal)
                        {
                            _transcripts.Add(tEvent);
                        }
                        await EmitAsync(tEvent).ConfigureAwait(false);
                        break;

                    case RealtimeBackendUpdate.AgentText text:
                        var uEvent = new StrategyEvent.AgentUtterance(primary.AgentId, text.Text, text.At);
                        _utterances.Add(uEvent);
                        await EmitAsync(uEvent).ConfigureAwait(false);
                        break;

                    case RealtimeBackendUpdate.FunctionCalled call:
                        await EmitAsync(new StrategyEvent.FunctionCalled(call.Name, call.Arguments, call.At))
                            .ConfigureAwait(false);
                        await HandleFunctionCallAsync(primary, call, ct).ConfigureAwait(false);
                        break;

                    case RealtimeBackendUpdate.Faulted fault:
                        _logger.LogWarning(fault.Exception, "Primary backend faulted: {Message}", fault.Message);
                        await EmitAsync(new StrategyEvent.Faulted(fault.Message, fault.Exception, fault.At)).ConfigureAwait(false);
                        return;
                }
            }
        }
        catch (OperationCanceledException) { /* primary swap or shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ensemble backend loop crashed for primary {AgentId}", primary.AgentId);
            await EmitAsync(new StrategyEvent.Faulted(ex.Message, ex, DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }
    }

    private async Task RunInsightsLoopAsync()
    {
        try
        {
            await foreach (var insight in _ensemble.Insights.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                _recentInsights.Add(insight);
                await EmitAsync(new StrategyEvent.DelegateInsight(insight.AgentId, insight.Summary, insight.Confidence, insight.At))
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ensemble insights loop terminated");
        }
    }

    /// <summary>
    /// Fans every freshly-buffered transcript / utterance / insight out to all delegates.
    /// Delegates can write new insights into the ensemble via the strategy's
    /// <see cref="DelegateInsightWriter"/> wrapper.
    /// </summary>
    private async Task RunDelegateContextLoopAsync()
    {
        // Subscribe to our own event stream by tee-ing through a private channel.
        // Cheaper than a pub-sub for the small set of delegates per call.
        var insightWriter = new DelegateInsightWriter(_ensemble);

        try
        {
            // Poll-based fanout: when a transcript or utterance is emitted, build a
            // fresh EnsembleContext snapshot and fire all delegates in parallel.
            // Buffer-driven so we don't double-process — we react to the recent buffers
            // changing rather than re-reading the unbounded event stream.
            int lastTranscriptVersion = -1;
            int lastUtteranceVersion = -1;

            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(50, _cts.Token).ConfigureAwait(false);

                var tVer = _transcripts.Version;
                var uVer = _utterances.Version;
                if (tVer == lastTranscriptVersion && uVer == lastUtteranceVersion)
                {
                    continue;
                }
                lastTranscriptVersion = tVer;
                lastUtteranceVersion = uVer;

                var ctx = new EnsembleContext(
                    _startContext!.CallId,
                    _transcripts.Snapshot(),
                    _utterances.Snapshot(),
                    _recentInsights.Snapshot());

                var delegates = _ensemble.Delegates;
                var tasks = delegates.Select(d => InvokeDelegateAsync(d, ctx, insightWriter, _cts.Token)).ToArray();
                if (tasks.Length > 0)
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Delegate context loop terminated");
        }
    }

    private async Task InvokeDelegateAsync(
        IDelegateAgent del,
        EnsembleContext ctx,
        DelegateInsightWriter insights,
        CancellationToken ct)
    {
        try
        {
            await del.OnContextAsync(ctx, insights, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Delegate {AgentId} threw inside OnContextAsync", del.AgentId);
        }
    }

    private ValueTask EmitAsync(StrategyEvent ev) => _events.Writer.WriteAsync(ev, CancellationToken.None);

    /// <summary>
    /// Push the current stage's prompt and guard-wrapped tool surface (including the
    /// synthesized <c>advance_to_*</c> functions from <see cref="IvrAdvanceFunctions"/>
    /// when the stage can advance) onto <paramref name="primary"/>, and emit
    /// <see cref="StrategyEvent.WorkflowStepEntered"/> for observers. Called on session
    /// start, after every primary swap, and after every successful navigator transition
    /// driven by an advance call.
    /// </summary>
    private async Task ApplyStageOnAsync(IConversationalAgent primary, RealtimeIvrWorkflowStep step, CancellationToken ct)
    {
        var tools = _navigator!.WrapToolsWithCurrentGuards(step.AvailableTools ?? []).ToList();

        if (!step.Terminal)
        {
            // Build a function set scoped to the current primary so each advance tool
            // re-arms the same backend it was pushed onto. The functions run under the
            // realtime client's UseFunctionInvocation() pipeline and return
            // AdvanceToolResult to the model.
            var advanceFunctions = new IvrAdvanceFunctions(
                _navigator,
                async (nextStep, innerCt) =>
                {
                    await ApplyStageOnAsync(primary, nextStep, innerCt).ConfigureAwait(false);
                    if (nextStep.Terminal)
                    {
                        _navigator.Complete();
                        await _cts.CancelAsync().ConfigureAwait(false);
                    }
                },
                _loggerFactory?.CreateLogger<IvrAdvanceFunctions>());

            tools.AddRange(advanceFunctions.BuildForStep(step));
        }

        await primary.Backend.UpdateToolsAsync(tools, ct).ConfigureAwait(false);

        var prompt = _navigator.BuildCurrentStepPrompt();
        await primary.Backend.UpdateSystemPromptAsync(prompt, ct).ConfigureAwait(false);

        await EmitAsync(new StrategyEvent.WorkflowStepEntered(step.Id, DateTimeOffset.UtcNow)).ConfigureAwait(false);
    }

    /// <summary>
    /// Surface backend tool invocations to observers. The IVR <c>advance_to_*</c>
    /// functions run inline via <see cref="IvrAdvanceFunctions"/> under the realtime
    /// client's function-invocation pipeline, so this method no longer mutates the
    /// navigator.
    /// </summary>
    private Task HandleFunctionCallAsync(IConversationalAgent primary, RealtimeBackendUpdate.FunctionCalled call, CancellationToken ct)
    {
        if (IvrAdvanceFunctions.IsAdvanceFunctionName(call.Name))
        {
            _logger.LogDebug(
                "Advance function '{Function}' fired on ensemble primary {AgentId}; transition handled inline by IvrAdvanceFunctions.",
                call.Name, primary.AgentId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Bridge that lets delegates publish insights into the ensemble's bus through
    /// the <see cref="ChannelWriter{T}"/> contract on <see cref="IDelegateAgent.OnContextAsync"/>.
    /// </summary>
    private sealed class DelegateInsightWriter : ChannelWriter<AgentInsight>
    {
        private readonly IAgentEnsemble _ensemble;

        public DelegateInsightWriter(IAgentEnsemble ensemble) { _ensemble = ensemble; }

        public override bool TryWrite(AgentInsight item)
        {
            // Synchronous publish path. DefaultAgentEnsemble's PublishInsightAsync is
            // backed by an unbounded channel so this never blocks.
            if (_ensemble is DefaultAgentEnsemble def)
            {
                _ = def.PublishInsightAsync(item);
                return true;
            }
            return false;
        }

        public override ValueTask WriteAsync(AgentInsight item, CancellationToken cancellationToken = default)
        {
            if (_ensemble is DefaultAgentEnsemble def)
            {
                return def.PublishInsightAsync(item, cancellationToken);
            }
            return ValueTask.CompletedTask;
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);
    }

    /// <summary>
    /// Tiny ring buffer that exposes a monotonically increasing <see cref="Version"/>
    /// so the delegate context loop can detect changes without re-reading the event stream.
    /// </summary>
    private sealed class RecentBuffer<T>
    {
        private readonly int _capacity;
        private readonly List<T> _items = [];
        private readonly Lock _lock = new();
        private int _version;

        public RecentBuffer(int capacity) { _capacity = capacity; }

        public int Version
        {
            get { lock (_lock) { return _version; } }
        }

        public void Add(T item)
        {
            lock (_lock)
            {
                if (_items.Count >= _capacity)
                {
                    _items.RemoveAt(0);
                }
                _items.Add(item);
                _version++;
            }
        }

        public IReadOnlyList<T> Snapshot()
        {
            lock (_lock)
            {
                return [.. _items];
            }
        }
    }
}
