using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Registry;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Telemetry;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly RealtimeIvrWorkflowDefinition _workflow;
    private readonly ILoggerFactory _loggerFactory;
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
    private IIvrWorkflowNavigator? _navigator;
    private Task? _agentLoop;
    private Task? _audioPump;
    private Task? _dtmfPump;
    private bool _suspended;
    private bool _prewarmed;
    private CallEdgeMetadata? _callerMetadata;
    private ConversationContext? _conversationContext;

    // Serializes stage transitions issued from the two concurrent producers in this
    // strategy: the realtime agent loop (advance-tool function calls) and the inbound
    // DTMF pump (menu / collect → transition). Both call ApplyStageAsync under this
    // lock so the navigator and backend prompt/tool surface stay coherent.
    private readonly SemaphoreSlim _navigatorLock = new(1, 1);

    // Buffered digit collection state for the inbound DTMF pump. Only used when the
    // current stage has a scripted.dtmf.collect block; reset on each successful
    // commit or stage transition.
    private readonly StringBuilder _dtmfBuffer = new();

    public RealtimeVoiceStrategy(
        IRealtimeVoiceBackend backend,
        RealtimeIvrWorkflowDefinition workflow,
        ILoggerFactory loggerFactory,
        CallingTelemetry telemetry,
        IvrWorkflowState? restoreFrom = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(telemetry);

        _backend = backend;
        _workflow = workflow;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RealtimeVoiceStrategy>();
        _telemetry = telemetry;

        WorkflowState = restoreFrom ?? new IvrWorkflowState { Status = IvrWorkflowStatus.Running };
    }

    public StrategyKind Kind => StrategyKind.RealtimeVoice;

    public AgentTier Tier => AgentTier.RealtimeVoice;

    public IvrWorkflowState WorkflowState { get; }

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
            await PrepareBackendAsync(context.Services, cancellationToken).ConfigureAwait(false);
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

        await PrepareBackendAsync(services, cancellationToken).ConfigureAwait(false);
        _prewarmed = true;
    }

    private async Task PrepareBackendAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        _navigator = new IvrWorkflowNavigator(
            _workflow,
            WorkflowState,
            services,
            _loggerFactory?.CreateLogger<IvrWorkflowNavigator>());

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
        if (_navigator is null)
        {
            throw new InvalidOperationException("Navigator must be built before pushing initial state.");
        }

        // Authenticate the caller (if any authenticators are registered) before the first
        // prompt push so the navigator's prompt can include the resolved identity.
        _conversationContext = await CallerAuthenticationRunner.RunAsync(
            services,
            _callId,
            _callerMetadata,
            _events.Writer,
            _logger,
            WorkflowState,
            cancellationToken).ConfigureAwait(false);

        // Seed the agent with the system prompt for the current workflow step.
        var step = _navigator.EnterInitialStep();
        await ApplyStageAsyncLocked(step, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>
    /// Serialized wrapper around <see cref="ApplyStageAsync"/> so the two concurrent
    /// producers (advance-tool function calls and the inbound DTMF pump) never race
    /// on the navigator + backend prompt/tool update sequence.
    /// </summary>
    private async Task ApplyStageAsyncLocked(RealtimeIvrWorkflowStep step, CancellationToken ct)
    {
        await _navigatorLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ApplyStageAsync(step, ct).ConfigureAwait(false);
        }
        finally
        {
            _navigatorLock.Release();
        }
    }

    /// <summary>
    /// Push the current step's prompt and guard-wrapped tool surface (including the
    /// synthesized <see cref="IvrAdvanceTool"/> when the step can advance) onto the
    /// realtime backend, and emit <see cref="StrategyEvent.WorkflowStepEntered"/> for
    /// observers. Called once on entry from <see cref="PushInitialStateAsync"/> and again
    /// after every successful navigator transition driven by an advance function call.
    /// </summary>
    private async Task ApplyStageAsync(RealtimeIvrWorkflowStep step, CancellationToken cancellationToken)
    {
        var tools = _navigator!.WrapToolsWithCurrentGuards(step.AvailableTools ?? []).ToList();

        if (!step.Terminal)
        {
            var advance = IvrAdvanceTool.TryCreate(step);
            if (advance is not null)
            {
                tools.Add(advance);
            }
        }

        //await _backend.UpdateToolsAsync(tools, cancellationToken).ConfigureAwait(false);

        var prompt = _navigator.BuildCurrentStepPrompt(_conversationContext);

        //await _backend.UpdateSystemPromptAsync(prompt, cancellationToken).ConfigureAwait(false);
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
    /// Pump inbound DTMF tones from the caller edge. Each digit is handled stage-aware
    /// when the current step has <c>scripted.dtmf</c> configuration (menu options or
    /// buffered <c>collect</c> validator), and otherwise forwarded to the realtime model
    /// as an inline user text turn so the LLM can react conversationally. Mirrors the
    /// logic in <see cref="Dtmf.DtmfStreamingStrategy"/> intentionally (copy-not-extract
    /// per the design call); a future refactor could pull this into a shared
    /// <c>DtmfInputProcessor</c> helper consumed by every non-DTMF strategy.
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

                await HandleDtmfToneAsync(tone, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Realtime inbound DTMF pump terminated for call {CallId}", _callId);
        }
    }
    #endregion

    private async Task HandleDtmfToneAsync(DtmfTone tone, CancellationToken ct)
    {
        var step = _navigator?.CurrentStep;
        if (step is null)
        {
            return;
        }

        // Always surface the digit for observability, even when we end up handling it
        // by simply forwarding to the LLM.
        await _events.Writer.WriteAsync(
            new StrategyEvent.DtmfRecognized(tone.Digit.ToString(), step.Id, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        var dtmf = step.StepScriptedConfiguration?.Dtmf;
        var hasMenu = dtmf?.MenuOptions is { Count: > 0 };
        var hasCollect = dtmf?.DigitCollectionValidator is not null
            || !string.IsNullOrEmpty(dtmf?.OnValidNextStepId);

        if (hasMenu)
        {
            await HandleMenuDigitAsync(step, tone.Digit, ct).ConfigureAwait(false);
            return;
        }

        if (hasCollect)
        {
            await HandleCollectedDigitAsync(step, dtmf!, tone.Digit, ct).ConfigureAwait(false);
            return;
        }

        // LLM-aware fallback: hand the digit to the model as a user text turn so it can
        // react in voice (e.g. "You pressed 1, let me look that up for you").
        try
        {
            await _backend.SendUserTextAsync($"[Caller pressed {tone.Digit}]", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Realtime backend rejected synthetic user text for DTMF digit '{Digit}' on step {StepId}",
                tone.Digit, step.Id);
        }
    }

    private async Task HandleMenuDigitAsync(RealtimeIvrWorkflowStep step, char digit, CancellationToken ct)
    {
        if (_navigator is null || !_navigator.TryResolveDtmfDigit(digit, out var option))
        {
            // Unrecognized digit on a menu stage — surface to the model as text so it
            // can prompt the caller to retry rather than ignoring silently.
            try
            {
                await _backend.SendUserTextAsync(
                    $"[Caller pressed {digit} — not a valid option on this menu]", ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to forward unmatched DTMF digit '{Digit}' to backend", digit);
            }
            return;
        }

        WorkflowState.Set($"{step.Id}_selection", option.Label);

        var actionResult = await _navigator.InvokeMenuActionAsync(option, extraArguments: null, ct).ConfigureAwait(false);
        await DispatchActionResultAsync(actionResult, step, ct).ConfigureAwait(false);
    }

    private async Task HandleCollectedDigitAsync(
        RealtimeIvrWorkflowStep step,
        StepDtmfConfiguration dtmf,
        char digit,
        CancellationToken ct)
    {
        var terminator = dtmf.TerminationDigitChar;
        var maxDigits = dtmf.MaxNumberOfDigits <= 0 ? int.MaxValue : dtmf.MaxNumberOfDigits;

        string? collected = null;
        lock (_dtmfBuffer)
        {
            if (digit == terminator)
            {
                collected = _dtmfBuffer.ToString();
                _dtmfBuffer.Clear();
            }
            else
            {
                _dtmfBuffer.Append(digit);
                if (_dtmfBuffer.Length >= maxDigits)
                {
                    collected = _dtmfBuffer.ToString();
                    _dtmfBuffer.Clear();
                }
            }
        }

        if (string.IsNullOrEmpty(collected))
        {
            return;
        }

        if (dtmf.MinNumberOfDigits > 0 && collected.Length < dtmf.MinNumberOfDigits)
        {
            // Too few digits — surface so LLM can prompt for completion.
            try
            {
                await _backend.SendUserTextAsync(
                    $"[Caller entered '{collected}' but {dtmf.MinNumberOfDigits} digits are required]",
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to forward incomplete DTMF buffer to backend");
            }
            return;
        }

        if (_navigator is null)
        {
            return;
        }

        if (dtmf.DigitCollectionValidator is { } validator)
        {
            var stateKey = dtmf.CollectedStateKey ?? $"{step.Id}_collected";
            var extra = new Dictionary<string, object?>
            {
                [dtmf.DigitsParameterName] = collected,
            };

            var actionResult = await _navigator.InvokeActionAsync(
                validator,
                dtmf.DigitCollectionArguments,
                extraArguments: extra,
                successNextStepId: dtmf.OnValidNextStepId,
                failurePrompt: dtmf.OnInvalidPrompt,
                failureAudio: dtmf.OnInvalidAudioFile,
                ct).ConfigureAwait(false);

            if (actionResult is DtmfActionResult.Transition or DtmfActionResult.Complete)
            {
                WorkflowState.Set(stateKey, collected);
            }

            await DispatchActionResultAsync(actionResult, step, ct).ConfigureAwait(false);
            return;
        }

        // No validator: store under the default key and walk the first transition, if any.
        WorkflowState.Set(dtmf.CollectedStateKey ?? $"{step.Id}_collected", collected);
        if (dtmf.OnValidNextStepId is { Length: > 0 } onValid)
        {
            await DispatchActionResultAsync(new DtmfActionResult.Transition(onValid), step, ct).ConfigureAwait(false);
        }
    }

    private async Task DispatchActionResultAsync(
        DtmfActionResult result,
        RealtimeIvrWorkflowStep step,
        CancellationToken ct)
    {
        switch (result)
        {
            case DtmfActionResult.Transition transition when _navigator is not null:
                var tr = _navigator.TransitionTo(transition.NextStepId);
                if (tr.Succeeded && tr.NewStep is not null)
                {
                    await ApplyStageAsyncLocked(tr.NewStep, ct).ConfigureAwait(false);
                    if (tr.NewStep.Terminal)
                    {
                        await EndSessionAsync($"terminal stage '{tr.NewStep.Id}' reached", ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "DTMF requested transition to '{Target}' but it was rejected: {Reason}",
                        transition.NextStepId, tr.Reason);
                }
                break;

            case DtmfActionResult.Reject reject:
                try
                {
                    var msg = !string.IsNullOrEmpty(reject.ErrorPrompt)
                        ? $"[DTMF input rejected: {reject.ErrorPrompt}]"
                        : $"[DTMF input rejected on step {step.Id}]";
                    await _backend.SendUserTextAsync(msg, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to surface DTMF rejection to backend");
                }
                break;

            case DtmfActionResult.Complete:
                await EndSessionAsync("DTMF action completed the workflow", ct).ConfigureAwait(false);
                break;

            case DtmfActionResult.Transfer transfer:
                await _outbound.Writer.WriteAsync(
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
                await EndSessionAsync("DTMF triggered transfer", ct).ConfigureAwait(false);
                break;

            case DtmfActionResult.HangUp:
                await EndSessionAsync("DTMF triggered hang-up", ct).ConfigureAwait(false);
                break;

            case DtmfActionResult.Escalate escalate:
                await _events.Writer.WriteAsync(
                    new StrategyEvent.EscalationRequested(escalate.Reason, DateTimeOffset.UtcNow),
                    ct).ConfigureAwait(false);
                break;

            case DtmfActionResult.Repeat:
                // Realtime tier: the model owns prompting; surfacing a hint lets it re-ask.
                try
                {
                    await _backend.SendUserTextAsync(
                        "[Caller selection unclear, please repeat the options]", ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to surface DTMF repeat to backend");
                }
                break;
        }
    }



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
                        await HandleFunctionCallAsync(call, ct).ConfigureAwait(false);
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
    /// Dispatch a tool invocation that came back from the realtime backend. Today the
    /// only orchestration tool we own is <see cref="IvrAdvanceTool"/>; other tool calls
    /// are surfaced as <see cref="StrategyEvent.FunctionCalled"/> and otherwise left to
    /// the backend's own function-invocation pipeline (the model will receive a
    /// <c>FunctionResultContent</c> by the time it observes them here).
    /// </summary>
    private async Task HandleFunctionCallAsync(RealtimeBackendUpdate.FunctionCalled call, CancellationToken ct)
    {
        if (!string.Equals(call.Name, IvrAdvanceTool.AdvanceToolName, StringComparison.Ordinal))
        {
            return;
        }

        if (_navigator?.CurrentStep is not { } currentStep)
        {
            _logger.LogWarning("Advance tool fired but no current step is set on call {CallId}", _callId);
            return;
        }

        var chosen = ExtractAdvanceChoice(call.Arguments);
        if (chosen is null)
        {
            _logger.LogWarning(
                "Advance tool fired on step {StepId} without a '{Arg}' argument; arguments: {Args}",
                currentStep.Id, IvrAdvanceTool.NextStageArgumentName, string.Join(",", call.Arguments.Keys));
            return;
        }

        var resolution = IvrAdvanceTool.Resolve(currentStep, chosen);
        if (!resolution.IsTransition || resolution.TargetStageId is not { Length: > 0 } target)
        {
            _logger.LogInformation(
                "Advance choice '{Chosen}' on step {StepId} resolved to {Kind}; not transitioning",
                chosen, currentStep.Id, resolution.Kind);
            return;
        }

        var result = _navigator.TransitionTo(target);
        if (!result.Succeeded || result.NewStep is null)
        {
            _logger.LogWarning(
                "Realtime advance to '{Target}' from '{Current}' rejected: {Reason}",
                target, currentStep.Id, result.Reason);
            return;
        }

        await ApplyStageAsyncLocked(result.NewStep, ct).ConfigureAwait(false);

        if (result.NewStep.Terminal)
        {
            await EndSessionAsync($"terminal stage '{result.NewStep.Id}' reached", ct).ConfigureAwait(false);
        }
    }

    private static string? ExtractAdvanceChoice(IReadOnlyDictionary<string, object?> arguments)
    {
        if (arguments.TryGetValue(IvrAdvanceTool.NextStageArgumentName, out var value) && value is not null)
        {
            return value.ToString();
        }

        // Defensive: some providers serialize arguments with quoting nuances; if the
        // dictionary holds exactly one argument fall back to that single value.
        if (arguments.Count == 1)
        {
            var single = arguments.Values.First();
            return single?.ToString();
        }

        return null;
    }

    /// <summary>
    /// Wind the strategy down when the workflow lands on a terminal stage. We mark the
    /// workflow complete, emit an <see cref="StrategyEvent.EscalationRequested"/>-style
    /// hint, and signal the agent loop to exit. The session host owns hang-up itself.
    /// </summary>
    private async Task EndSessionAsync(string reason, CancellationToken ct)
    {
        _navigator?.Complete();

        await _events.Writer.WriteAsync(
            new StrategyEvent.AgentUtterance(_backend.AgentId, $"[session ending: {reason}]", DateTimeOffset.UtcNow),
            CancellationToken.None).ConfigureAwait(false);

        // Close the agent loop so the session moves to teardown. Use a separate cancel so
        // the in-flight ApplyStageAsync above can complete cleanly.
        await _cts.CancelAsync().ConfigureAwait(false);
    }
}
