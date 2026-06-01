using System.Text;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Agents.IntentAgent;
using Agents.AI.ContactCenter.Calling.Strategies.Dtmf;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Telemetry;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Agents.AI.ContactCenter.Calling.Strategies.Composite;
using Agents.AI.ContactCenter.Calling.Strategies.RealtimeVoice;

namespace Agents.AI.ContactCenter.Calling.Strategies.Nlu;

/// <summary>
/// Tier 3 strategy: caller audio → <see cref="IvrIntentAgent"/> → workflow transition → TTS.
/// The intent agent owns speech recognition, JSON intent classification, and any local tool
/// dispatch; this strategy is purely an orchestration layer that drives the IVR workflow
/// from the agent's emitted <see cref="IvrIntentEvent"/> stream.
/// </summary>
/// <remarks>
/// <para>
/// Designed as a degradation target between <see cref="RealtimeVoiceStrategy"/> (Tier 0) and
/// <see cref="DtmfStreamingStrategy"/> (Tier 4) inside a <see cref="CompositeFallbackStrategy"/>.
/// The composite preserves <see cref="IvrWorkflowState"/> across swaps via <c>restoreFrom</c>,
/// and per-call scoped services such as <see cref="CallerAuthenticationState"/> are shared
/// regardless of which strategy is currently active.
/// </para>
/// <para>
/// Pairs with streaming caller edges (e.g. <c>AcsCallerEdge</c>): forwards inbound PCM frames
/// to <see cref="IvrIntentAgent.ClassifyAudioStreamAsync(System.Collections.Generic.IAsyncEnumerable{System.ReadOnlyMemory{byte}}, System.Func{IvrIntentClassificationContext}, System.Threading.CancellationToken)"/>
/// and produces synthesized PCM via <c>OutboundDirective.Audio</c>. To escalate, emits an
/// <c>OutboundDirective.TransferCall</c> when an utterance classifies as the well-known
/// transfer intent (<see cref="TransferIntentName"/>).
/// </para>
/// </remarks>
public sealed class NluConversationStrategy : IConversationStrategy
{
    /// <summary>
    /// Reserved intent name. When an utterance classifies as this intent the strategy emits
    /// a <see cref="OutboundDirective.TransferCall"/> using <see cref="EscalationTarget"/>.
    /// </summary>
    public const string TransferIntentName = "transfer_to_agent";

    private readonly IvrWorkflowSession _session;
    private readonly IvrIntentAgent _intentAgent;
    private readonly ISpeechSynthesizer _synthesizer;
    private readonly ILogger _logger;

    private readonly Channel<OutboundDirective> _outbound = Channel.CreateBounded<OutboundDirective>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly Channel<StrategyEvent> _events = Channel.CreateUnbounded<StrategyEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly Channel<ReadOnlyMemory<byte>> _audioFrames = Channel.CreateBounded<ReadOnlyMemory<byte>>(
        new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly CancellationTokenSource _cts = new();
    private DtmfInputProcessor? _dtmfProcessor;
    private Task? _audioPump;
    private Task? _classifyLoop;
    private Task? _dtmfPump;
    private bool _suspended;
    private string _callId = string.Empty;
    private CallEdgeMetadata? _callerMetadata;

    // When a DTMF press resolves an intent / transition, suppress the very next
    // no-match event in the speech classifier loop — the caller already gave us
    // a deterministic answer, we don't want to re-prompt over it.
    private int _suppressNoMatchCount;

    public NluConversationStrategy(
        IvrWorkflowSession session,
        IvrIntentAgent intentAgent,
        ISpeechSynthesizer synthesizer,
        TransferEscalationTarget? escalationTarget = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(intentAgent);
        ArgumentNullException.ThrowIfNull(synthesizer);

        _session = session;
        _intentAgent = intentAgent;
        _synthesizer = synthesizer;
        EscalationTarget = escalationTarget;
        _logger = loggerFactory?.CreateLogger<NluConversationStrategy>()
                  ?? NullLogger<NluConversationStrategy>.Instance;
    }

    public StrategyKind Kind => StrategyKind.Nlu;

    public AgentTier Tier => AgentTier.IntentNlu;

    public IvrWorkflowState WorkflowState => _session.State;

    public EdgeCapabilities EmittedDirectives =>
        EdgeCapabilities.Audio | EdgeCapabilities.StopPlayback | EdgeCapabilities.TransferCall;

    public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;

    public ChannelReader<StrategyEvent> Events => _events.Reader;

    /// <summary>Where the strategy transfers when the caller's intent is <see cref="TransferIntentName"/>.</summary>
    public TransferEscalationTarget? EscalationTarget { get; }

    public Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default)
    {
        if (_classifyLoop is not null) { return Task.CompletedTask; }

        _callId = context.CallId;
        _callerMetadata = context.CallerMetadata;

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _classifyLoop = Task.Run(() => RunAsync(context, linked.Token), CancellationToken.None);
        _audioPump = Task.Run(() => PumpInboundAudioAsync(context, linked.Token), CancellationToken.None);
        _dtmfPump = Task.Run(() => PumpInboundDtmfAsync(context, linked.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _audioFrames.Writer.TryComplete();
        if (_audioPump is not null)
        {
            try { await _audioPump.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        if (_classifyLoop is not null)
        {
            try { await _classifyLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        if (_dtmfPump is not null)
        {
            try { await _dtmfPump.ConfigureAwait(false); } catch { /* shutdown */ }
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
        _cts.Dispose();
    }

    private async Task PumpInboundAudioAsync(StrategyStartContext context, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in context.InboundAudio.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_suspended) { continue; }
                await _audioFrames.Writer.WriteAsync(frame.Pcm, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NLU inbound audio pump terminated for call {CallId}", _callId);
        }
        finally
        {
            _audioFrames.Writer.TryComplete();
        }
    }

    private async Task RunAsync(StrategyStartContext context, CancellationToken ct)
    {
        // Build the shared DTMF input processor now that we know the strategy is alive.
        // The sink delegates speech-flow side effects (speak/reject/repeat) back to this
        // strategy's own TTS helpers.
        _dtmfProcessor = new DtmfInputProcessor(
            _session,
            new NluDtmfSink(this),
            _events.Writer,
            _logger);

        try
        {
            await CallerAuthenticationRunner.RunAsync(
                context.Services,
                _callId,
                _callerMetadata,
                _events.Writer,
                _logger,
                _session.State,
                ct).ConfigureAwait(false);

            // If the composite restored mid-workflow, re-enter the current step. Otherwise start fresh.
            var step = _session.Navigator.ResumeCurrentStep() ?? _session.Navigator.EnterInitialStep();
            await EnterStepWithGuardsAsync(step, ct).ConfigureAwait(false);

            await foreach (var evt in _intentAgent
                .ClassifyAudioStreamAsync(_audioFrames.Reader.ReadAllAsync(ct), BuildContext, ct)
                .ConfigureAwait(false))
            {
                if (!evt.Transcript.IsFinal || string.IsNullOrWhiteSpace(evt.Transcript.Text))
                {
                    continue;
                }

                await _events.Writer.WriteAsync(
                    new StrategyEvent.Transcript("caller", evt.Transcript.Text, IsFinal: true, DateTimeOffset.UtcNow),
                    ct).ConfigureAwait(false);

                if (_suspended) { continue; }
                await ProcessIntentEventAsync(evt, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NLU strategy faulted for call {CallId}", context.CallId);
            await _events.Writer.WriteAsync(
                new StrategyEvent.Faulted(ex.Message, ex, DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _outbound.Writer.TryComplete();
            _events.Writer.TryComplete();
        }
    }

    private RealtimeIvrWorkflowStep ResumeOrEnterInitialStep()
        => _session.Navigator.ResumeCurrentStep() ?? _session.Navigator.EnterInitialStep();

    /// <summary>
    /// Resolves the per-utterance classification context the intent agent should use for
    /// the next final transcript. Called once per utterance by the agent's streaming
    /// classification loop so the candidate intent set tracks the live workflow step.
    /// </summary>
    private IvrIntentClassificationContext BuildContext()
    {
        var step = _session.Navigator.CurrentStep ?? ResumeOrEnterInitialStep();
        var stepIntents = step.Intents;
        var validIntents = new List<string>(stepIntents.Count + 1);
        foreach (var intentName in stepIntents.Keys)
        {
            validIntents.Add(intentName);
        }
        if (EscalationTarget is not null && !validIntents.Contains(TransferIntentName))
        {
            validIntents.Add(TransferIntentName);
        }

        // Tools are dispatched by the strategy (workflow transitions, transfer) rather than
        // by the agent itself for the IVR path, so we hand it an empty tool catalog.
        return new IvrIntentClassificationContext(
            Utterance: string.Empty,
            ValidIntents: validIntents,
            Tools: Array.Empty<Microsoft.Extensions.AI.AITool>(),
            IntentToolMap: null);
    }

    private async Task ProcessIntentEventAsync(IvrIntentEvent evt, CancellationToken ct)
    {
        var step = _session.Navigator.CurrentStep ?? ResumeOrEnterInitialStep();
        var stepIntents = step.Intents;

        if (stepIntents.Count == 0 && EscalationTarget is null)
        {
            _logger.LogDebug("NLU step {StepId} has no intents; storing utterance and re-prompting", step.Id);
            _session.State.Set(step.Id, evt.Transcript.Text);
            await SpeakStepPromptAsync(step, ct).ConfigureAwait(false);
            return;
        }

        var result = evt.Intent;
        if (result.IsNone)
        {
            // If a DTMF press just resolved a stage, swallow the upcoming no-match
            // event so we don't re-prompt the caller after they've already answered.
            if (Interlocked.Exchange(ref _suppressNoMatchCount, Math.Max(0, _suppressNoMatchCount - 1)) > 0)
            {
                _logger.LogDebug(
                    "Suppressing no-match prompt on step {StepId} because DTMF input already resolved the stage",
                    step.Id);
                return;
            }

            _logger.LogInformation(
                "No intent matched on step {StepId} for utterance: {Utterance}", step.Id, evt.Transcript.Text);
            await EmitConfiguredPromptAsync(
                step.StepScriptedConfiguration?.OnErrorPrompt,
                step.StepScriptedConfiguration?.OnErrorAudioFile,
                fallbackText: "I didn't understand that. Could you say it another way?",
                ct).ConfigureAwait(false);
            return;
        }

        await _events.Writer.WriteAsync(
            new StrategyEvent.IntentClassified(result.IntentName!, result.Confidence, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        if (result.Entities is not null)
        {
            foreach (var (k, v) in result.Entities)
            {
                _session.State.Set(k, v);
            }
        }

        if (string.Equals(result.IntentName, TransferIntentName, StringComparison.Ordinal))
        {
            await EscalateAsync(result.Entities?.GetValueOrDefault("reason") ?? "Caller requested an agent.", ct).ConfigureAwait(false);
            return;
        }

        // Look up the resolved intent so we can route via its declared next stage and
        // honour any confirmation prompt the author attached.
        if (!stepIntents.TryGetValue(result.IntentName!, out var intent) || intent.NextStepId is not { Length: > 0 } targetStage)
        {
            _logger.LogWarning(
                "Intent '{Intent}' classified for step {StepId} but no nextStage is declared; re-prompting",
                result.IntentName, step.Id);
            await SpeakStepPromptAsync(step, ct).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(intent.ConfirmPrompt))
        {
            // The orchestration layer owns confirmation. For now we speak the prompt and
            // commit the transition; a future enhancement can wait for a second confirming
            // utterance before advancing.
            await SpeakAsync(intent.ConfirmPrompt!, ct).ConfigureAwait(false);
        }
        else if (step.StepScriptedConfiguration is { } scriptedCfg
            && (!string.IsNullOrWhiteSpace(scriptedCfg.OnConfirmPrompt) || scriptedCfg.OnConfirmAudioFile is not null))
        {
            await EmitConfiguredPromptAsync(scriptedCfg.OnConfirmPrompt, scriptedCfg.OnConfirmAudioFile, fallbackText: null, ct).ConfigureAwait(false);
        }

        // Phase 3 parity with RealtimeVoice: route the speech-driven transition through
        // EvaluateTransitionAsync so per-transition `requires:` guards and workflow-level
        // auth-resolver detours fire on the NLU tier too.
        var eval = await _session.Navigator.EvaluateTransitionAsync(targetStage, ct).ConfigureAwait(false);
        switch (eval)
        {
            case TransitionEvaluation.Allowed allowed:
            {
                var tr = _session.Navigator.TransitionTo(allowed.Target.Id);
                if (!tr.Succeeded || tr.NewStep is null)
                {
                    _logger.LogWarning(
                        "Intent '{Intent}' classified for step {StepId} but transition to '{Target}' failed: {Reason}",
                        result.IntentName, step.Id, targetStage, tr.Reason);
                    await SpeakAsync("Let's try that again.", ct).ConfigureAwait(false);
                    await SpeakStepPromptAsync(step, ct).ConfigureAwait(false);
                    return;
                }
                await EnterStepWithGuardsAsync(tr.NewStep, ct).ConfigureAwait(false);
                break;
            }

            case TransitionEvaluation.RequiresDetour detour:
            {
                _session.State.Set(
                    PendingIntent.StateKey,
                    new PendingIntent(detour.Target.Id, _session.Navigator.Definition.Name, result.IntentName));
                _logger.LogInformation(
                    "Intent '{Intent}' detouring through '{Subflow}' to satisfy '{Guard}'.",
                    result.IntentName, detour.ResolverWorkflowId, detour.UnmetGuard.GetType().Name);
                var childInitial = await _session.Navigator.PushSubflowAsync(
                    detour.ResolverWorkflowId,
                    returnToStepId: detour.Target.Id,
                    failureReturnStepId: detour.Target.OnUnauthorizedStepId
                        ?? _session.Navigator.Definition.UnauthorizedFailureStepId,
                    detour.MinVersion,
                    detour.MaxVersion,
                    ct).ConfigureAwait(false);
                await EnterStepWithGuardsAsync(childInitial, ct).ConfigureAwait(false);
                break;
            }

            case TransitionEvaluation.BlockedNoResolver blocked:
                _logger.LogWarning(
                    "Intent '{Intent}' transition to '{Target}' blocked: {Reason}",
                    result.IntentName, targetStage, blocked.Reason);
                await SpeakAsync("I'm not able to do that right now.", ct).ConfigureAwait(false);
                await SpeakStepPromptAsync(step, ct).ConfigureAwait(false);
                break;

            case TransitionEvaluation.Invalid invalid:
                _logger.LogWarning(
                    "Intent '{Intent}' classified for step {StepId} but transition to '{Target}' was rejected: {Reason}",
                    result.IntentName, step.Id, targetStage, invalid.Reason);
                await SpeakAsync("Let's try that again.", ct).ConfigureAwait(false);
                await SpeakStepPromptAsync(step, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Apply <paramref name="step"/> through <see cref="IIvrWorkflowNavigator.EnterStepAsync"/>
    /// so subflow markers / terminal-child pops are handled uniformly, then render the
    /// resolved step.
    /// </summary>
    private async Task EnterStepWithGuardsAsync(RealtimeIvrWorkflowStep step, CancellationToken ct)
    {
        var resolved = await _session.Navigator.EnterStepAsync(step, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            _logger.LogInformation("NLU strategy reached a terminal root stage on call {CallId}; completing.", _callId);
            return;
        }
        await EmitStepEnteredAsync(resolved, ct).ConfigureAwait(false);
        await SpeakStepPromptAsync(resolved, ct).ConfigureAwait(false);
    }

    private async Task EscalateAsync(string reason, CancellationToken ct)
    {
        if (EscalationTarget is null)
        {
            _logger.LogWarning("Caller requested transfer but no escalation target is configured for call {CallId}", _callId);
            await SpeakAsync("I'm unable to transfer you right now.", ct).ConfigureAwait(false);
            return;
        }

        await _events.Writer.WriteAsync(
            new StrategyEvent.EscalationRequested(reason, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);
        var handoffStep = _session.Navigator.CurrentStep;
        await EmitConfiguredPromptAsync(
            handoffStep?.StepScriptedConfiguration?.OnHandoffPrompt,
            handoffStep?.StepScriptedConfiguration?.OnHandoffAudioFile,
            fallbackText: "Transferring you to an agent now. Please hold.",
            ct).ConfigureAwait(false);
        await _outbound.Writer.WriteAsync(
            new OutboundDirective.TransferCall(
                EscalationTarget.TargetIdentifier,
                EscalationTarget.Kind,
                DateTimeOffset.UtcNow,
                reason),
            ct).ConfigureAwait(false);
        _session.Complete();
    }

    private async Task EmitStepEnteredAsync(RealtimeIvrWorkflowStep step, CancellationToken ct)
    {
        await _events.Writer.WriteAsync(
            new StrategyEvent.WorkflowStepEntered(step.Id, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);
    }

    private async Task SpeakStepPromptAsync(RealtimeIvrWorkflowStep step, CancellationToken ct)
    {
        // Resolve entry prompt: Nlu sub-config override > shared scripted parent.
        var scripted = step.StepScriptedConfiguration;
        var nluCfg = scripted?.Nlu;
        var ssml = nluCfg?.SsmlPromptOverride ?? scripted?.SsmlPrompt;
        var audio = nluCfg?.AudioFile ?? scripted?.AudioFile;
        if (audio is not null || !string.IsNullOrWhiteSpace(ssml))
        {
            await EmitConfiguredPromptAsync(ssml, audio, fallbackText: null, ct).ConfigureAwait(false);
            return;
        }

        var prompt = step.ConversationState.Description
            ?? step.ConversationState.Goal
            ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt)) { return; }
        await SpeakAsync(prompt, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits the highest-priority directive among the supplied overrides:
    /// pre-recorded audio file -> SSML / plain-text override -> <paramref name="fallbackText"/>.
    /// SSML is detected by a leading <c>&lt;speak</c> token and sent to the synthesizer as
    /// <see cref="SynthesizerInputFormat.SSML"/>.
    /// </summary>
    private async Task EmitConfiguredPromptAsync(string? promptOrSsml, Uri? audioFile, string? fallbackText, CancellationToken ct)
    {
        if (audioFile is not null)
        {
            await _outbound.Writer.WriteAsync(
                new OutboundDirective.PlayFile(audioFile, DateTimeOffset.UtcNow),
                ct).ConfigureAwait(false);
            await _events.Writer.WriteAsync(
                new StrategyEvent.AgentUtterance("nlu", $"[audio:{audioFile}]", DateTimeOffset.UtcNow),
                ct).ConfigureAwait(false);
            return;
        }

        var text = !string.IsNullOrWhiteSpace(promptOrSsml) ? promptOrSsml : fallbackText;
        if (string.IsNullOrWhiteSpace(text)) { return; }

        var format = LooksLikeSsml(text) ? SynthesizerInputFormat.SSML : SynthesizerInputFormat.Text;
        await SpeakAsync(text!, format, ct).ConfigureAwait(false);
    }

    private Task SpeakAsync(string text, CancellationToken ct) =>
        SpeakAsync(text, SynthesizerInputFormat.Text, ct);

    private async Task SpeakAsync(string text, SynthesizerInputFormat format, CancellationToken ct)
    {
        try
        {
            await foreach (var pcm in _synthesizer.SynthesizeAsync(text, format, ct).ConfigureAwait(false))
            {
                if (_suspended) { break; }
                await _outbound.Writer.WriteAsync(
                    new OutboundDirective.Audio(new AudioFrame(pcm, DateTimeOffset.UtcNow, SourceEdgeId: null)),
                    ct).ConfigureAwait(false);
            }

            await _events.Writer.WriteAsync(
                new StrategyEvent.AgentUtterance("nlu", text, DateTimeOffset.UtcNow),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TTS synthesis failed in NLU strategy for call {CallId}", _callId);
        }
    }

    internal static bool LooksLikeSsml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return false; }
        var span = text.AsSpan().TrimStart();
        return span.StartsWith("<speak", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pump inbound DTMF tones through the shared <see cref="Dtmf.DtmfInputProcessor"/>.
    /// Per-strategy nuances (speak via TTS rather than forwarding text to an LLM, suppress
    /// the next no-match prompt after a DTMF resolution) live in <see cref="NluDtmfSink"/>.
    /// </summary>
    private async Task PumpInboundDtmfAsync(StrategyStartContext context, CancellationToken ct)
    {
        try
        {
            await foreach (var tone in context.InboundDtmf.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_suspended) { continue; }
                if (_dtmfProcessor is not null)
                {
                    await _dtmfProcessor.ProcessAsync(tone, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NLU inbound DTMF pump terminated for call {CallId}", _callId);
        }
    }

    /// <summary>
    /// NLU flavor of <see cref="IDtmfStrategySink"/>. Speaks via TTS for caller-facing
    /// surfaces and increments the no-match suppression counter so the classifier loop
    /// doesn't re-prompt over a DTMF-resolved stage.
    /// </summary>
    private sealed class NluDtmfSink(NluConversationStrategy strategy) : IDtmfStrategySink
    {
        public async Task ApplyStepAsync(RealtimeIvrWorkflowStep step, CancellationToken ct)
        {
            Interlocked.Increment(ref strategy._suppressNoMatchCount);
            await strategy.EnterStepWithGuardsAsync(step, ct).ConfigureAwait(false);
        }

        public Task RejectAsync(DtmfActionResult.Reject reject, RealtimeIvrWorkflowStep step, CancellationToken ct)
            => strategy.EmitConfiguredPromptAsync(reject.ErrorPrompt, reject.ErrorAudioFile, fallbackText: null, ct);

        public Task RepeatAsync(DtmfActionResult.Repeat repeat, RealtimeIvrWorkflowStep step, CancellationToken ct)
            => strategy.SpeakStepPromptAsync(step, ct);

        public Task EndSessionAsync(string reason, CancellationToken ct)
        {
            strategy._session.Complete();
            return Task.CompletedTask;
        }

        public async Task TransferAsync(DtmfActionResult.Transfer transfer, CancellationToken ct)
        {
            await strategy._events.Writer.WriteAsync(
                new StrategyEvent.EscalationRequested(transfer.Reason ?? "DTMF transfer", DateTimeOffset.UtcNow),
                ct).ConfigureAwait(false);
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
            strategy._session.Complete();
        }

        public Task EscalateAsync(string reason, CancellationToken ct)
            => strategy.EscalateAsync(reason, ct);

        public Task OnUnmatchedMenuDigitAsync(DtmfTone tone, RealtimeIvrWorkflowStep step, CancellationToken ct)
        {
            strategy._logger.LogDebug(
                "Ignoring unmapped menu digit '{Digit}' on step {StepId}; speech classifier still active",
                tone.Digit, step.Id);
            return Task.CompletedTask;
        }

        public Task OnUnconfiguredDigitAsync(DtmfTone tone, RealtimeIvrWorkflowStep step, CancellationToken ct)
        {
            strategy._logger.LogDebug(
                "Ignoring inbound DTMF digit '{Digit}' on step {StepId}: no menu / collect / nlu mapping",
                tone.Digit, step.Id);
            return Task.CompletedTask;
        }

        public Task OnIncompleteBufferAsync(string collected, int minRequired, RealtimeIvrWorkflowStep step, CancellationToken ct)
        {
            strategy._logger.LogDebug(
                "DTMF buffer '{Collected}' on step {StepId} shorter than min {Min}; discarding",
                collected, step.Id, minRequired);
            return Task.CompletedTask;
        }

        public Task OnTransitionBlockedAsync(string targetStepId, string reason, RealtimeIvrWorkflowStep step, CancellationToken ct)
            => strategy.SpeakAsync($"I can't continue to that yet: {reason}", ct);
    }
}

/// <summary>
/// Where to send the call when an utterance classifies as the transfer intent or when a
/// composite chain falls through to an escalation step.
/// </summary>
/// <param name="TargetIdentifier">E.164 number, Teams user id, or ACS user id depending on <paramref name="Kind"/>.</param>
/// <param name="Kind">How the platform should interpret <paramref name="TargetIdentifier"/>.</param>
public sealed record TransferEscalationTarget(string TargetIdentifier, TransferKind Kind = TransferKind.BlindToPhoneNumber);

