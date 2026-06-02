using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling.Strategies.Dtmf;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Registry;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Telemetry;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Calling.Strategies.RealtimeVoice;

/// <summary>
/// Tier 0 strategy: native realtime audio-to-audio model. Ports
/// <see cref="Transports.RealtimeVoiceAgentTransport"/> onto the new
/// <see cref="IConversationStrategy"/> contract via <see cref="IRealtimeVoiceBackend"/>.
/// </summary>
public sealed class RealtimeVoiceStrategy : IConversationStrategy
{
    private readonly IRealtimeVoiceBackend _backend;
    private readonly IvrWorkflowSession _session;
    private readonly ILogger _logger;
    private readonly CallingTelemetry _telemetry;
    private string _callId = string.Empty;

    private readonly Channel<OutboundDirective> _outbound = Channel.CreateBounded<OutboundDirective>(
        new BoundedChannelOptions(500)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly Channel<StrategyEvent> _events = Channel.CreateUnbounded<StrategyEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource _cts = new();
    private readonly IvrAdvanceFunctions _advanceFunctions;
    private readonly DtmfInputProcessor _dtmfProcessor;
    private Task? _agentLoop;
    private Task? _audioPump;
    private Task? _dtmfPump;
    private bool _suspended;
    private bool _prewarmed;
    private CallEdgeMetadata? _callerMetadata;
    private ConversationContext? _conversationContext;

    // Serializes stage transitions issued from the two concurrent producers in this
    // strategy: the realtime agent loop (advance-tool function calls) and the inbound
    // DTMF pump (menu / collect → transition). Both call ApplyStepAsync under this
    // lock so the navigator and backend prompt/tool surface stay coherent.
    private readonly SemaphoreSlim _navigatorLock = new(1, 1);

    public RealtimeVoiceStrategy(
        IRealtimeVoiceBackend backend,
        IvrWorkflowSession session,
        ILoggerFactory loggerFactory,
        CallingTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(telemetry);

        _backend = backend;
        _session = session;
        _logger = loggerFactory.CreateLogger<RealtimeVoiceStrategy>();
        _telemetry = telemetry;

        // Bind the advance-function builder now that we know the apply pipeline; the
        // session returns the same instance on subsequent calls.
        _advanceFunctions = _session.GetOrCreateAdvanceFunctions(ApplyStepAsync);

        // Build the shared DTMF input processor. Strategy-specific side effects are
        // routed through the realtime sink (forward as inline LLM text turns).
        _dtmfProcessor = new DtmfInputProcessor(
            _session,
            new RealtimeVoiceDtmfSink(this),
            _events.Writer,
            _logger);

    }

    public StrategyKind Kind => StrategyKind.RealtimeVoice;

    public AgentTier Tier => AgentTier.RealtimeVoice;

    public IvrWorkflowState WorkflowState => _session.State;

    public EdgeCapabilities EmittedDirectives => EdgeCapabilities.Audio | EdgeCapabilities.StopPlayback;

    public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;

    public ChannelReader<StrategyEvent> Events => _events.Reader;

    public async Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default)
    {
        if (_agentLoop is not null)
        {
            return;
        }

        _callId = context.CallId;
        _callerMetadata = context.CallerMetadata;

        if (!_prewarmed)
        {
            await ConnectBackendAsync(cancellationToken).ConfigureAwait(false);
        }


        // Authentication and the first prompt push need caller metadata, so they always run
        // here in StartAsync — even when the backend was prewarmed without an attached edge.
        await PushInitialStateAsync(context.Services, cancellationToken).ConfigureAwait(false);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _audioPump = Task.Run(() => PumpInboundAudioAsync(context, linked.Token), CancellationToken.None);
        _dtmfPump = Task.Run(() => PumpInboundDtmfAsync(context, linked.Token), CancellationToken.None);
        _agentLoop = Task.Run(() => RunAgentLoopAsync(linked.Token), CancellationToken.None);
    }

    public async ValueTask PrewarmAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (_prewarmed)
        {
            return;
        }

        await ConnectBackendAsync(cancellationToken).ConfigureAwait(false);
        _prewarmed = true;
    }

    private async Task ConnectBackendAsync(CancellationToken cancellationToken)
    {
        using var connectSpan = _telemetry.StartChildActivity("contact_center.strategy.backend.connect", _callId);
        try
        {
            await _backend.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CallingActivitySource.SetError(connectSpan, ex);
            throw;
        }
    }

    private async Task PushInitialStateAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        // Authenticate the caller (if any authenticators are registered) before the first
        // prompt push so the navigator's prompt can include the resolved identity.
        _conversationContext = await CallerAuthenticationRunner.RunAsync(
            services,
            _callId,
            _callerMetadata,
            _events.Writer,
            _logger,
            _session.State,
            cancellationToken).ConfigureAwait(false);

        // Resume from a restored state if present (tier swap), otherwise start fresh.
        var step = _session.Navigator.ResumeCurrentStep() ?? _session.Navigator.EnterInitialStep();
        await ApplyStepAsync(step, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Single serialized stage-entry pipeline. Calls
    /// <see cref="IIvrWorkflowNavigator.EnterStepAsync(RealtimeIvrWorkflowStep, CancellationToken)"/>
    /// to resolve the step through any subflow pushes / terminal-child pops, then renders
    /// the resulting prompt + tool surface onto the realtime backend. A <see langword="null"/>
    /// return from the navigator means the workflow ended at a terminal root stage — we
    /// tear the session down.
    /// </summary>
    private async Task ApplyStepAsync(RealtimeIvrWorkflowStep step, CancellationToken cancellationToken)
    {
        // Stage transitions are atomic; the lock acquisition itself must not be cancellable
        // or a partially-applied stage can leave the navigator and backend out of sync.
        await _navigatorLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var resolved = await _session.Navigator.EnterStepAsync(step, cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                // Subflow / terminal-child pop chained all the way up and exhausted the
                // frame stack — nothing left to render.
                await EndSessionAsync($"workflow ended while resolving '{step.Id}'", cancellationToken).ConfigureAwait(false);
                return;
            }

            await RenderStepAsync(resolved, cancellationToken).ConfigureAwait(false);

            // Terminal root stage: render once (so observers see the final WorkflowStepEntered)
            // then wind the session down. EnterStepAsync already marked the navigator complete.
            if (_session.State.IsComplete)
            {
                await EndSessionAsync($"terminal stage '{resolved.Id}' reached", cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _navigatorLock.Release();
        }
    }

    /// <summary>
    /// Push the resolved step's prompt and guard-wrapped tool surface (including the
    /// synthesized <c>advance_to_*</c> functions from <see cref="IvrAdvanceFunctions"/>
    /// when the step can advance) onto the realtime backend, and emit
    /// <see cref="StrategyEvent.WorkflowStepEntered"/> for observers.
    /// </summary>
    private async Task RenderStepAsync(RealtimeIvrWorkflowStep step, CancellationToken cancellationToken)
    {
        var tools = _session.Navigator.WrapToolsWithCurrentGuards(step.AvailableTools ?? []).ToList();

        if (!step.Terminal)
        {
            tools.AddRange(_advanceFunctions.BuildForStep(step));
        }

        var prompt = _session.Navigator.BuildCurrentStepPrompt(_conversationContext);

        await _backend.StartResponseAsync(tools, prompt, cancellationToken).ConfigureAwait(false);

        await _events.Writer.WriteAsync(
            new StrategyEvent.WorkflowStepEntered(step.Id, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        await _events.Writer.WriteAsync(
            new StrategyEvent.AgentSpeakingChanged(_backend.AgentId, _backend.AgentDisplayName, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_audioPump is not null)
        {
            try { await _audioPump.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        if (_dtmfPump is not null)
        {
            try { await _dtmfPump.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        if (_agentLoop is not null)
        {
            try { await _agentLoop.ConfigureAwait(false); } catch { /* shutdown */ }
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
        await _backend.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    #region Pump loops
    private async Task PumpInboundAudioAsync(StrategyStartContext context, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in context.InboundAudio.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_suspended)
                {
                    continue;
                }
                await _backend.SendAudioAsync(frame.Pcm, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Realtime inbound audio pump terminated");
        }
    }

    /// <summary>
    /// Pump inbound DTMF tones from the caller edge through the shared
    /// <see cref="Dtmf.DtmfInputProcessor"/>. Per-strategy nuances (forwarding
    /// unrecognized digits to the realtime model as inline user text turns,
    /// surfacing rejections as inline LLM hints, etc.) live in
    /// <see cref="RealtimeVoiceDtmfSink"/>.
    /// </summary>
    private async Task PumpInboundDtmfAsync(StrategyStartContext context, CancellationToken ct)
    {
        try
        {
            await foreach (var tone in context.InboundDtmf.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_suspended)
                {
                    continue;
                }

                await _dtmfProcessor.ProcessAsync(tone, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Realtime inbound DTMF pump terminated for call {CallId}", _callId);
        }
    }
    #endregion

    private async Task RunAgentLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var update in _backend.RunAsync(ct).ConfigureAwait(false))
            {
                switch (update)
                {
                    case RealtimeBackendUpdate.Audio audio when !_suspended:
                        await _outbound.Writer.WriteAsync(
                            new OutboundDirective.Audio(
                                new AudioFrame(audio.Pcm, audio.At, SourceEdgeId: _backend.AgentId)),
                            ct).ConfigureAwait(false);
                        break;

                    case RealtimeBackendUpdate.Transcript transcript:
                        await _events.Writer.WriteAsync(
                            new StrategyEvent.Transcript(transcript.Speaker, transcript.Text, transcript.IsFinal, transcript.At),
                            ct).ConfigureAwait(false);
                        break;

                    case RealtimeBackendUpdate.AgentText text:
                        await _events.Writer.WriteAsync(
                            new StrategyEvent.AgentUtterance(_backend.AgentId, text.Text, text.At),
                            ct).ConfigureAwait(false);
                        break;

                    case RealtimeBackendUpdate.FunctionCalled call:
                        await _events.Writer.WriteAsync(
                            new StrategyEvent.FunctionCalled(call.Name, call.Arguments, call.At),
                            ct).ConfigureAwait(false);
                        break;

                    case RealtimeBackendUpdate.UserSpeechStarted speech when !_suspended:
                        // Caller started speaking. Tell the caller edge to stop playing any
                        // queued agent audio so we don't talk over the caller (barge-in).
                        await _outbound.Writer.WriteAsync(
                            new OutboundDirective.StopPlayback(speech.At),
                            ct).ConfigureAwait(false);
                        break;

                    case RealtimeBackendUpdate.Faulted fault:
                        _logger.LogWarning(fault.Exception, "Realtime backend faulted: {Message}", fault.Message);
                        await _events.Writer.WriteAsync(
                            new StrategyEvent.Faulted(fault.Message, fault.Exception, fault.At),
                            CancellationToken.None).ConfigureAwait(false);
                        return; // give the composite a chance to swap us out
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Realtime agent loop crashed");
            using (var faultSpan = _telemetry.StartChildActivity("contact_center.strategy.agent_loop.faulted", _callId))
            {
                CallingActivitySource.SetError(faultSpan, ex);
            }
            await _events.Writer.WriteAsync(
                new StrategyEvent.Faulted(ex.Message, ex, DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wind the strategy down when the workflow lands on a terminal stage. We mark the
    /// workflow complete, emit an <see cref="StrategyEvent.EscalationRequested"/>-style
    /// hint, and signal the agent loop to exit. The session host owns hang-up itself.
    /// </summary>
    private async Task EndSessionAsync(string reason, CancellationToken ct)
    {
        _session.Complete();

        await _events.Writer.WriteAsync(
            new StrategyEvent.AgentUtterance(_backend.AgentId, $"[session ending: {reason}]", DateTimeOffset.UtcNow),
            CancellationToken.None).ConfigureAwait(false);

        // Close the agent loop so the session moves to teardown. Use a separate cancel so
        // the in-flight ApplyStageAsync above can complete cleanly.
        await _cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Realtime-voice flavor of <see cref="IDtmfStrategySink"/>. All caller-facing side
    /// effects surface as inline user-text turns into the realtime backend so the model
    /// can react conversationally; transitions re-enter the navigator-aware
    /// <see cref="ApplyStepAsync"/> pipeline so subflow / terminal routing stays uniform
    /// with the advance-tool path.
    /// </summary>
    private sealed class RealtimeVoiceDtmfSink(RealtimeVoiceStrategy strategy) : IDtmfStrategySink
    {
        public Task ApplyStepAsync(RealtimeIvrWorkflowStep step, CancellationToken ct)
            => strategy.ApplyStepAsync(step, ct);

        public Task RejectAsync(DtmfActionResult.Reject reject, RealtimeIvrWorkflowStep step, CancellationToken ct)
            => SendBackendNoteAsync(
                !string.IsNullOrEmpty(reject.ErrorPrompt)
                    ? $"[DTMF input rejected: {reject.ErrorPrompt}]"
                    : $"[DTMF input rejected on step {step.Id}]",
                ct);

        public Task RepeatAsync(DtmfActionResult.Repeat repeat, RealtimeIvrWorkflowStep step, CancellationToken ct)
            => SendBackendNoteAsync("[Caller selection unclear, please repeat the options]", ct);

        public Task EndSessionAsync(string reason, CancellationToken ct)
            => strategy.EndSessionAsync(reason, ct);

        public async Task TransferAsync(DtmfActionResult.Transfer transfer, CancellationToken ct)
        {
            await strategy._outbound.Writer.WriteAsync(
                new OutboundDirective.TransferCall(
                    transfer.TargetIdentifier,
                    transfer.Kind switch
                    {
                        TransferKindHint.TeamsUser => TransferKind.BlindToTeamsUser,
                        TransferKindHint.Consultative => TransferKind.Consultative,
                        _ => TransferKind.BlindToPhoneNumber,
                    },
                    DateTimeOffset.UtcNow,
                    transfer.Reason),
                ct).ConfigureAwait(false);
            await strategy.EndSessionAsync("DTMF triggered transfer", ct).ConfigureAwait(false);
        }

        public Task EscalateAsync(string reason, CancellationToken ct)
            => strategy._events.Writer.WriteAsync(
                new StrategyEvent.EscalationRequested(reason, DateTimeOffset.UtcNow),
                ct).AsTask();

        public Task OnUnmatchedMenuDigitAsync(DtmfTone tone, RealtimeIvrWorkflowStep step, CancellationToken ct)
            => SendBackendNoteAsync($"[Caller pressed {tone.Digit} — not a valid option on this menu]", ct);

        public Task OnUnconfiguredDigitAsync(DtmfTone tone, RealtimeIvrWorkflowStep step, CancellationToken ct)
            => SendBackendNoteAsync($"[Caller pressed {tone.Digit}]", ct);

        public Task OnIncompleteBufferAsync(string collected, int minRequired, RealtimeIvrWorkflowStep step, CancellationToken ct)
            => SendBackendNoteAsync(
                $"[Caller entered '{collected}' but {minRequired} digits are required]",
                ct);

        public Task OnTransitionBlockedAsync(string targetStepId, string reason, RealtimeIvrWorkflowStep step, CancellationToken ct)
            => SendBackendNoteAsync($"[Transition to '{targetStepId}' blocked: {reason}]", ct);

        private async Task SendBackendNoteAsync(string note, CancellationToken ct)
        {
            try
            {
                await strategy._backend.SendUserTextAsync(note, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                strategy._logger.LogWarning(ex, "Failed to surface DTMF note to backend: {Note}", note);
            }
        }
    }
}
