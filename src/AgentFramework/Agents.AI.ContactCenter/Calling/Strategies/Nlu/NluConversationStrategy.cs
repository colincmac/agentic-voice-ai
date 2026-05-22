using System.Threading.Channels;
using Agents.AI.ContactCenter.Agents.IntentAgent;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Telemetry;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Agents.AI.ContactCenter.Calling.Strategies.Composite;
using Agents.AI.ContactCenter.Calling.Strategies.Dtmf;
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

    private readonly RealtimeIvrWorkflowDefinition _workflow;
    private readonly IvrIntentAgent _intentAgent;
    private readonly ISpeechSynthesizer _synthesizer;
    private readonly ILoggerFactory? _loggerFactory;
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
    private IIvrWorkflowNavigator? _navigator;
    private Task? _audioPump;
    private Task? _classifyLoop;
    private bool _suspended;
    private string _callId = string.Empty;
    private CallEdgeMetadata? _callerMetadata;

    public NluConversationStrategy(
        RealtimeIvrWorkflowDefinition workflow,
        IvrIntentAgent intentAgent,
        ISpeechSynthesizer synthesizer,
        IvrWorkflowState? restoreFrom = null,
        TransferEscalationTarget? escalationTarget = null,
        ILoggerFactory? loggerFactory = null)
    {
        _workflow = workflow;
        _intentAgent = intentAgent;
        _synthesizer = synthesizer;
        EscalationTarget = escalationTarget;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<NluConversationStrategy>()
                  ?? NullLogger<NluConversationStrategy>.Instance;

        WorkflowState = restoreFrom ?? new IvrWorkflowState { Status = IvrWorkflowStatus.Running };
    }

    public StrategyKind Kind => StrategyKind.Nlu;

    public AgentTier Tier => AgentTier.IntentNlu;

    public IvrWorkflowState WorkflowState { get; }

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
        _navigator = new IvrWorkflowNavigator(
            _workflow,
            WorkflowState,
            context.Services,
            _loggerFactory?.CreateLogger<IvrWorkflowNavigator>());

        try
        {
            await CallerAuthenticationRunner.RunAsync(
                context.Services,
                _callId,
                _callerMetadata,
                _events.Writer,
                _logger,
                WorkflowState,
                ct).ConfigureAwait(false);

            // If the composite restored mid-workflow, re-enter the current step. Otherwise start fresh.
            var step = ResumeOrEnterInitialStep();
            await EmitStepEnteredAsync(step, ct).ConfigureAwait(false);
            await SpeakStepPromptAsync(step, ct).ConfigureAwait(false);

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
    {
        // Restored from a previous tier: workflow state already has CurrentStepName.
        if (WorkflowState.CurrentStepName is { Length: > 0 } current
            && _workflow.GetStep(current) is { } resumed)
        {
            _logger.LogInformation("NLU strategy resuming on step {StepId} for call {CallId}", current, _callId);
            return resumed;
        }
        return _navigator!.EnterInitialStep();
    }

    /// <summary>
    /// Resolves the per-utterance classification context the intent agent should use for
    /// the next final transcript. Called once per utterance by the agent's streaming
    /// classification loop so the candidate intent set tracks the live workflow step.
    /// </summary>
    private IvrIntentClassificationContext BuildContext()
    {
        var step = _navigator?.CurrentStep ?? ResumeOrEnterInitialStep();
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
        var step = _navigator?.CurrentStep ?? ResumeOrEnterInitialStep();
        var stepIntents = step.Intents;

        if (stepIntents.Count == 0 && EscalationTarget is null)
        {
            _logger.LogDebug("NLU step {StepId} has no intents; storing utterance and re-prompting", step.Id);
            WorkflowState.Set(step.Id, evt.Transcript.Text);
            await SpeakStepPromptAsync(step, ct).ConfigureAwait(false);
            return;
        }

        var result = evt.Intent;
        if (result.IsNone)
        {
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
                WorkflowState.Set(k, v);
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

        var transition = _navigator!.TransitionTo(targetStage);
        if (!transition.Succeeded || transition.NewStep is null)
        {
            _logger.LogWarning(
                "Intent '{Intent}' classified for step {StepId} but transition to '{Target}' failed: {Reason}",
                result.IntentName, step.Id, targetStage, transition.Reason);
            await SpeakAsync("Let's try that again.", ct).ConfigureAwait(false);
            await SpeakStepPromptAsync(step, ct).ConfigureAwait(false);
            return;
        }

        await EmitStepEnteredAsync(transition.NewStep, ct).ConfigureAwait(false);
        await SpeakStepPromptAsync(transition.NewStep, ct).ConfigureAwait(false);
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
        var handoffStep = _navigator?.CurrentStep;
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
        _navigator?.Complete();
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
}

/// <summary>
/// Where to send the call when an utterance classifies as the transfer intent or when a
/// composite chain falls through to an escalation step.
/// </summary>
/// <param name="TargetIdentifier">E.164 number, Teams user id, or ACS user id depending on <paramref name="Kind"/>.</param>
/// <param name="Kind">How the platform should interpret <paramref name="TargetIdentifier"/>.</param>
public sealed record TransferEscalationTarget(string TargetIdentifier, TransferKind Kind = TransferKind.BlindToPhoneNumber);
