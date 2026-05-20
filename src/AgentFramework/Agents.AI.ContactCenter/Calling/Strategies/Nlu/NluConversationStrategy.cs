using System.Threading.Channels;
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
/// Tier 3 strategy: STT → <see cref="IIntentClassifier"/> → workflow transition → TTS.
/// Uses the deterministic intent matcher to drive the IVR — no generative model is invoked.
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
/// Pairs with streaming caller edges (e.g. <c>AcsCallerEdge</c>): consumes inbound PCM through
/// the recognizer and produces synthesized PCM via <c>OutboundDirective.Audio</c>. To escalate,
/// emits an <c>OutboundDirective.TransferCall</c> when an utterance classifies as the
/// well-known transfer intent (<see cref="TransferIntentName"/>).
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
    private readonly ISpeechRecognizer _recognizer;
    private readonly ISpeechSynthesizer _synthesizer;
    private readonly IIntentClassifier _classifier;
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

    private readonly CancellationTokenSource _cts = new();
    private IIvrWorkflowNavigator? _navigator;
    private Task? _recognizerWritePump;
    private Task? _classifyLoop;
    private bool _suspended;
    private string _callId = string.Empty;
    private CallEdgeMetadata? _callerMetadata;

    public NluConversationStrategy(
        RealtimeIvrWorkflowDefinition workflow,
        ISpeechRecognizer recognizer,
        ISpeechSynthesizer synthesizer,
        IIntentClassifier classifier,
        IvrWorkflowState? restoreFrom = null,
        TransferEscalationTarget? escalationTarget = null,
        ILoggerFactory? loggerFactory = null)
    {
        _workflow = workflow;
        _recognizer = recognizer;
        _synthesizer = synthesizer;
        _classifier = classifier;
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
        _recognizerWritePump = Task.Run(() => PumpRecognizerAsync(context, linked.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_recognizerWritePump is not null)
        {
            try { await _recognizerWritePump.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        if (_classifyLoop is not null)
        {
            try { await _classifyLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        try { await _recognizer.CompleteAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
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
        try { await _recognizer.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        _cts.Dispose();
    }

    private async Task PumpRecognizerAsync(StrategyStartContext context, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in context.InboundAudio.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_suspended) { continue; }
                await _recognizer.WriteAudioAsync(frame.Pcm, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NLU recognizer write pump terminated for call {CallId}", _callId);
        }
        finally
        {
            try { await _recognizer.CompleteAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
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
                WorkflowState,
                telemetry: null,
                logger: _logger,
                cancellationToken: ct).ConfigureAwait(false);

            // If the composite restored mid-workflow, re-enter the current step. Otherwise start fresh.
            var step = ResumeOrEnterInitialStep();
            await EmitStepEnteredAsync(step, ct).ConfigureAwait(false);
            await SpeakStepPromptAsync(step, ct).ConfigureAwait(false);

            await foreach (var segment in _recognizer.GetTranscriptsAsync(ct).ConfigureAwait(false))
            {
                if (!segment.IsFinal || string.IsNullOrWhiteSpace(segment.Text))
                {
                    continue;
                }

                await _events.Writer.WriteAsync(
                    new StrategyEvent.Transcript("caller", segment.Text, IsFinal: true, DateTimeOffset.UtcNow),
                    ct).ConfigureAwait(false);

                if (_suspended) { continue; }
                await ProcessUtteranceAsync(segment.Text, ct).ConfigureAwait(false);
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

    private async Task ProcessUtteranceAsync(string utterance, CancellationToken ct)
    {
        var step = _navigator?.CurrentStep ?? ResumeOrEnterInitialStep();

        // Build the candidate intent set: every named intent on the current step plus the
        // well-known transfer intent (so the workflow author doesn't have to model it).
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

        if (validIntents.Count == 0)
        {
            _logger.LogDebug("NLU step {StepId} has no intents; storing utterance and re-prompting", step.Id);
            WorkflowState.Set(step.Id, utterance);
            await SpeakStepPromptAsync(step, ct).ConfigureAwait(false);
            return;
        }

        var result = await _classifier.ClassifyAsync(utterance, validIntents, ct).ConfigureAwait(false);
        if (result.IsNone)
        {
            _logger.LogInformation("No intent matched on step {StepId} for utterance: {Utterance}", step.Id, utterance);
            await SpeakAsync("I didn't understand that. Could you say it another way?", ct).ConfigureAwait(false);
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
        await SpeakAsync("Transferring you to an agent now. Please hold.", ct).ConfigureAwait(false);
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
        var prompt = step.ConversationState.Description
            ?? step.ConversationState.Goal
            ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt)) { return; }
        await SpeakAsync(prompt, ct).ConfigureAwait(false);
    }

    private async Task SpeakAsync(string text, CancellationToken ct)
    {
        try
        {
            await foreach (var pcm in _synthesizer.SynthesizeAsync(text, SynthesizerInputFormat.Text, ct).ConfigureAwait(false))
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
}

/// <summary>
/// Where to send the call when an utterance classifies as the transfer intent or when a
/// composite chain falls through to an escalation step.
/// </summary>
/// <param name="TargetIdentifier">E.164 number, Teams user id, or ACS user id depending on <paramref name="Kind"/>.</param>
/// <param name="Kind">How the platform should interpret <paramref name="TargetIdentifier"/>.</param>
public sealed record TransferEscalationTarget(string TargetIdentifier, TransferKind Kind = TransferKind.BlindToPhoneNumber);
