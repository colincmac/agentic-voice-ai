using System.Text;
using System.Threading.Channels;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Telemetry;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Calling.Strategies.Dtmf;

/// <summary>
/// Tier 4 strategy: DTMF menu navigation driven directly by <see cref="RealtimeIvrWorkflowDefinition"/>.
/// Ports <see cref="Transports.DtmfIvrTransport"/> onto <see cref="IConversationStrategy"/>.
/// </summary>
public sealed class DtmfStreamingStrategy : IConversationStrategy
{
    private readonly RealtimeIvrWorkflowDefinition _workflow;
    private readonly ISpeechSynthesizer? _synthesizer;
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

    private Task? _runLoop;
    private IIvrWorkflowNavigator? _navigator;
    private readonly StringBuilder _digitBuffer = new();

    private bool _suspended;
    private bool _prewarmed;
    private List<OutboundDirective>? _prewarmedInitialDirectives;
    private RealtimeIvrWorkflowStep? _prewarmedInitialStep;
    private DateTime _lastDtmfReceivedTime = DateTime.MinValue;
    private CancellationTokenSource? _interDigitTimeoutCts;
    private int _interDigitTimeoutMs = 5000;
    private readonly char _terminationDigitChar = '#';
    private readonly Lock _stateLock = new();
    private CallEdgeMetadata? _callerMetadata;
    private string _callId = string.Empty;

    public DtmfStreamingStrategy(
        RealtimeIvrWorkflowDefinition workflow,
        ISpeechSynthesizer? synthesizer = null,
        IvrWorkflowState? restoreFrom = null,
        ILoggerFactory? loggerFactory = null)
    {
        _workflow = workflow;
        _synthesizer = synthesizer;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<DtmfStreamingStrategy>() ?? NullLogger<DtmfStreamingStrategy>.Instance;

        WorkflowState = restoreFrom ?? new IvrWorkflowState { Status = IvrWorkflowStatus.Running };
    }

    public StrategyKind Kind => StrategyKind.Dtmf;

    public AgentTier Tier => AgentTier.DtmfOnly;

    public IvrWorkflowState WorkflowState { get; }

    public EdgeCapabilities EmittedDirectives => EdgeCapabilities.Audio | EdgeCapabilities.StopPlayback;

    public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;

    public ChannelReader<StrategyEvent> Events => _events.Reader;

    public Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default)
    {
        if (_runLoop is not null)
        {
            return Task.CompletedTask;
        }

        _callId = context.CallId;
        _callerMetadata = context.CallerMetadata;

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _runLoop = Task.Run(() => RunAsync(context, linked.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async ValueTask PrewarmAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (_prewarmed)
        {
            return;
        }

        _navigator = new IvrWorkflowNavigator(
            _workflow,
            WorkflowState,
            services,
            _loggerFactory?.CreateLogger<IvrWorkflowNavigator>());

        var initial = _navigator.EnterInitialStep();
        _prewarmedInitialStep = initial;

        // Pre-synthesize the first prompt so the caller hears the initial step
        // the moment StartAsync runs, instead of waiting for TTS round-trips.
        _prewarmedInitialDirectives = await BuildSpeakDirectivesAsync(initial, cancellationToken).ConfigureAwait(false);

        await _events.Writer.WriteAsync(
            new StrategyEvent.WorkflowStepEntered(initial.Id, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        if (_synthesizer is not null)
        {
            var (prompt, _) = BuildPrompt(initial);
            await _events.Writer.WriteAsync(
                new StrategyEvent.AgentUtterance("dtmf", prompt, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }

        _prewarmed = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_runLoop is not null)
        {
            try { await _runLoop.ConfigureAwait(false); } catch { /* swallow on shutdown */ }
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

    private async Task RunAsync(StrategyStartContext context, CancellationToken ct)
    {
        if (!_prewarmed)
        {
            _navigator = new IvrWorkflowNavigator(
                _workflow,
                WorkflowState,
                context.Services,
                _loggerFactory?.CreateLogger<IvrWorkflowNavigator>());
        }

        try
        {
            // Authenticate the caller (if any authenticators are registered) before running the
            // workflow so step guards / validators can read WorkflowState.AuthLevel and observers
            // see the CallerIdentified event before the first DtmfRecognized.
            await CallerAuthenticationRunner.RunAsync(
                context.Services,
                _callId,
                _callerMetadata,
                _events.Writer,
                WorkflowState,
                telemetry: null,
                logger: _logger,
                cancellationToken: ct).ConfigureAwait(false);

            if (_prewarmed && _prewarmedInitialStep is not null)
            {
                // Replay buffered prompt audio/directives — no TTS round-trip on the call's critical path.
                if (_prewarmedInitialDirectives is { Count: > 0 } buffered)
                {
                    foreach (var directive in buffered)
                    {
                        if (_suspended)
                        {
                            break;
                        }
                        await _outbound.Writer.WriteAsync(directive, ct).ConfigureAwait(false);
                    }
                    _prewarmedInitialDirectives = null;
                }
            }
            else
            {
                var initial = _navigator!.EnterInitialStep();
                await EnterStepAsync(initial, ct).ConfigureAwait(false);
            }

            await foreach (var tone in context.InboundDtmf.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_suspended)
                {
                    continue;
                }
                await HandleDigitAsync(tone, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DTMF strategy faulted for call {CallId}", context.CallId);
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

    private async Task PublishDtmfRecognizedAsync(string digits, CancellationToken ct)
    {
        var stepId = WorkflowState.CurrentStepName;
        if (stepId is null)
        {
            _logger.LogWarning("Received DTMF input but no current step is set");
            return;
        }
        _logger.LogInformation("Recognized DTMF input '{Digits}' for step {StepId}", digits, stepId);
        await _events.Writer.WriteAsync(
            new StrategyEvent.DtmfRecognized(stepId, digits, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);
    }

    private async Task HandleDigitAsync(DtmfTone tone, CancellationToken ct)
    {
        var digit = tone.Digit;
        var step = _navigator?.CurrentStep;
        if (step is null)
        {
            return;
        }

        await PublishDtmfRecognizedAsync(digit.ToString(), ct).ConfigureAwait(false);

        var dtmf = step.StepScriptedConfiguration?.Dtmf;
        var hasMenu = dtmf?.MenuOptions is { Count: > 0 };

        if (hasMenu)
        {
            await ProcessMenuSelectionAsync(step, digit, ct).ConfigureAwait(false);
        }
        else
        {
            await ProcessDigitCollectionAsync(step, digit, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessMenuSelectionAsync(RealtimeIvrWorkflowStep step, char digit, CancellationToken ct)
    {
        if (!_navigator!.TryResolveDtmfDigit(digit, out var option))
        {
            // Unrecognized digit — re-prompt.
            await SpeakAsync(step, ct).ConfigureAwait(false);
            return;
        }

        WorkflowState.Set($"{step.Id}_selection", option.Label);

        var actionResult = await _navigator.InvokeMenuActionAsync(option, extraArguments: null, ct).ConfigureAwait(false);
        await DispatchAsync(actionResult, step, ct).ConfigureAwait(false);
    }

    private async Task ProcessDigitCollectionAsync(RealtimeIvrWorkflowStep step, char digit, CancellationToken ct)
    {
        var dtmf = step.StepScriptedConfiguration?.Dtmf;
        string? collected = null;
        var now = DateTime.UtcNow;
        var maxDigits = dtmf?.MaxNumberOfDigits ?? int.MaxValue;
        if (maxDigits <= 0)
        {
            maxDigits = int.MaxValue;
        }

        lock (_stateLock)
        {
            if (digit == _terminationDigitChar)
            {
                CancelInterDigitTimeoutTimer();
                collected = _digitBuffer.ToString();
                _digitBuffer.Clear();
            }
            else
            {
                if (_lastDtmfReceivedTime != DateTime.MinValue)
                {
                    var timeSinceLastDtmf = (now - _lastDtmfReceivedTime).TotalMilliseconds;

                    if (timeSinceLastDtmf > _interDigitTimeoutMs)
                    {
                        collected = _digitBuffer.ToString();
                        _digitBuffer.Clear();
                    }
                }
                CancelInterDigitTimeoutTimer();

                _digitBuffer.Append(digit);
                _lastDtmfReceivedTime = now;

                if (_digitBuffer.Length >= maxDigits)
                {
                    collected = _digitBuffer.ToString();
                    _digitBuffer.Clear();
                    _lastDtmfReceivedTime = DateTime.MinValue;
                }
                else
                {
                    StartInterDigitTimeoutTimer(_interDigitTimeoutMs);
                }
            }
        }

        if (string.IsNullOrEmpty(collected))
        {
            return;
        }

        // Validator path: developer-defined tool decides what happens next.
        if (dtmf?.DigitCollectionValidator is { } validator)
        {
            var min = dtmf.MinNumberOfDigits;
            if (min > 0 && collected.Length < min)
            {
                await DispatchAsync(
                    new DtmfActionResult.Reject(dtmf.OnInvalidPrompt, dtmf.OnInvalidAudioFile),
                    step,
                    ct).ConfigureAwait(false);
                return;
            }

            var stateKey = dtmf.CollectedStateKey ?? $"{step.Id}_collected";
            var extra = new Dictionary<string, object?>
            {
                [dtmf.DigitsParameterName] = collected,
            };

            var actionResult = await _navigator!.InvokeActionAsync(
                validator,
                dtmf.DigitCollectionArguments,
                extraArguments: extra,
                successNextStepId: dtmf.OnValidNextStepId,
                failurePrompt: dtmf.OnInvalidPrompt,
                failureAudio: dtmf.OnInvalidAudioFile,
                ct).ConfigureAwait(false);

            // Store the digits on success so downstream steps can read them.
            if (actionResult is DtmfActionResult.Transition or DtmfActionResult.Complete)
            {
                WorkflowState.Set(stateKey, collected);
            }

            await DispatchAsync(actionResult, step, ct).ConfigureAwait(false);
            return;
        }

        // No validator: store under the default key and walk the first transition.
        WorkflowState.Set($"{step.Id}_collected", collected);
        var transitions = step.ValidTransitions;
        if (transitions.Count > 0)
        {
            await DispatchAsync(new DtmfActionResult.Transition(transitions[0]), step, ct).ConfigureAwait(false);
        }
    }

    private async Task EnterStepAsync(RealtimeIvrWorkflowStep step, CancellationToken ct)
    {
        await _events.Writer.WriteAsync(
            new StrategyEvent.WorkflowStepEntered(step.Id, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        await SpeakAsync(step, ct).ConfigureAwait(false);
    }

    private async Task SpeakAsync(RealtimeIvrWorkflowStep step, CancellationToken ct)
    {
        try
        {
            var directives = await BuildSpeakDirectivesAsync(step, ct).ConfigureAwait(false);

            foreach (var directive in directives)
            {
                if (_suspended)
                {
                    break;
                }
                await _outbound.Writer.WriteAsync(directive, ct).ConfigureAwait(false);
            }

            if (_synthesizer is not null && directives.Count > 0)
            {
                var (prompt, _) = BuildPrompt(step);
                await _events.Writer.WriteAsync(
                    new StrategyEvent.AgentUtterance("dtmf", prompt, DateTimeOffset.UtcNow),
                    ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TTS synthesis failed for DTMF strategy");
        }
    }

    /// <summary>
    /// Build the outbound directives that render the step's prompt — synthesizing PCM
    /// up-front when an <see cref="ISpeechSynthesizer"/> is available, or falling back to
    /// platform speak/play directives. Used by both the live <see cref="SpeakAsync"/> path
    /// and <see cref="PrewarmAsync"/> so the first prompt can be queued before the call is live.
    /// </summary>
    private async Task<List<OutboundDirective>> BuildSpeakDirectivesAsync(RealtimeIvrWorkflowStep step, CancellationToken ct)
    {
        var directives = new List<OutboundDirective>();

        if (_synthesizer is not null)
        {
            var (prompt, format) = BuildPrompt(step);
            await foreach (var pcm in _synthesizer.SynthesizeAsync(prompt, format, ct).ConfigureAwait(false))
            {
                directives.Add(new OutboundDirective.Audio(
                    new AudioFrame(pcm, DateTimeOffset.UtcNow, SourceEdgeId: null)));
            }
        }
        else if ((step.StepScriptedConfiguration?.Dtmf?.AudioFile ?? step.StepScriptedConfiguration?.AudioFile) is Uri fileUri)
        {
            directives.Add(new OutboundDirective.PlayFile(FileUri: fileUri, DateTimeOffset.UtcNow));
        }
        else if ((step.StepScriptedConfiguration?.Dtmf?.SsmlPromptOverride ?? step.StepScriptedConfiguration?.SsmlPrompt) is string entrySsml
            && !string.IsNullOrEmpty(entrySsml))
        {
            directives.Add(new OutboundDirective.SpeakText(entrySsml, DateTimeOffset.UtcNow));
        }

        return directives;
    }

    private static (string prompt, SynthesizerInputFormat format) BuildPrompt(RealtimeIvrWorkflowStep step)
    {
        var entrySsml = step.StepScriptedConfiguration?.Dtmf?.SsmlPromptOverride
            ?? step.StepScriptedConfiguration?.SsmlPrompt;
        if (entrySsml is not null)
        {
            return (entrySsml, SynthesizerInputFormat.SSML);
        }

        var prompt = new StringBuilder(step.ConversationState.Description ?? step.ConversationState.Goal ?? string.Empty);

        if (step.StepScriptedConfiguration?.Dtmf?.MenuOptions is { Count: > 0 } menu)
        {
            var menuText = string.Join(". ", menu.Select(kv => $"For {kv.Value.Label} press {kv.Key}"));
            prompt.AppendLine(menuText);
        }

        return (prompt.ToString(), SynthesizerInputFormat.Text);
    }

    /// <summary>
    /// Starts the inter-digit timeout. If timeout expires, publishes the current buffer.
    /// Must be called under _stateLock.
    /// </summary>
    private void StartInterDigitTimeoutTimer(int timeoutMs)
    {
        // Already under lock, create new CTS
        _interDigitTimeoutCts = new CancellationTokenSource();
        var token = _interDigitTimeoutCts.Token;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(timeoutMs, token);

                    string? collected = null;

                    lock (_stateLock)
                    {
                        if (!token.IsCancellationRequested && _digitBuffer.Length > 0)
                        {
                            _logger.LogInformation(
                                "Timeout was triggered between digit entry: TimeoutMs: {TimeoutMs}, Collected Digit Length: {BufferLength}",
                                timeoutMs,
                                _digitBuffer.Length);

                            collected = _digitBuffer.ToString();
                            _digitBuffer.Clear();
                        }
                        else
                        {
                            collected = null;
                        }
                    }

                    // PublishAsync outside the lock
                    if (!string.IsNullOrEmpty(collected))
                    {
                        await PublishDtmfRecognizedAsync(collected, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (TaskCanceledException)
                {
                    _logger.LogDebug("Inter-digit timeout cancelled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in inter-digit timeout task");
                }
            },
            CancellationToken.None); // Use None to avoid external cancellation affecting the task spawn
    }


    private void CancelInterDigitTimeoutTimer()
    {
        var cts = _interDigitTimeoutCts;
        _interDigitTimeoutCts = null;

        if (cts != null)
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed, ignore
            }
        }
    }

    private async Task DispatchAsync(DtmfActionResult result, RealtimeIvrWorkflowStep step, CancellationToken ct)
    {
        switch (result)
        {
            case DtmfActionResult.Transition transition:
                var tr = _navigator!.TransitionTo(transition.NextStepId);
                if (tr.Succeeded)
                {
                    await EnterStepAsync(tr.NewStep!, ct).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogWarning(
                        "DTMF action requested transition to '{StepId}' but it was rejected: {Reason}.",
                        transition.NextStepId, tr.Reason);
                }
                break;

            case DtmfActionResult.Repeat repeat:
                if (repeat.AudioFile is { } audioUri)
                {
                    await _outbound.Writer.WriteAsync(
                        new OutboundDirective.PlayFile(audioUri, DateTimeOffset.UtcNow),
                        ct).ConfigureAwait(false);
                }
                else if (!string.IsNullOrEmpty(repeat.Prompt))
                {
                    await _outbound.Writer.WriteAsync(
                        new OutboundDirective.SpeakText(repeat.Prompt, DateTimeOffset.UtcNow),
                        ct).ConfigureAwait(false);
                }
                else
                {
                    await SpeakAsync(step, ct).ConfigureAwait(false);
                }
                break;

            case DtmfActionResult.Reject reject:
                if (reject.ErrorAudioFile is { } errAudio)
                {
                    await _outbound.Writer.WriteAsync(
                        new OutboundDirective.PlayFile(errAudio, DateTimeOffset.UtcNow),
                        ct).ConfigureAwait(false);
                }
                else if (!string.IsNullOrEmpty(reject.ErrorPrompt))
                {
                    await _outbound.Writer.WriteAsync(
                        new OutboundDirective.SpeakText(reject.ErrorPrompt, DateTimeOffset.UtcNow),
                        ct).ConfigureAwait(false);
                }
                else if (step.StepScriptedConfiguration?.OnErrorAudioFile is { } stepErrAudio)
                {
                    await _outbound.Writer.WriteAsync(
                        new OutboundDirective.PlayFile(stepErrAudio, DateTimeOffset.UtcNow),
                        ct).ConfigureAwait(false);
                }
                else if (!string.IsNullOrEmpty(step.StepScriptedConfiguration?.OnErrorPrompt))
                {
                    await _outbound.Writer.WriteAsync(
                        new OutboundDirective.SpeakText(step.StepScriptedConfiguration!.OnErrorPrompt!, DateTimeOffset.UtcNow),
                        ct).ConfigureAwait(false);
                }
                else
                {
                    await SpeakAsync(step, ct).ConfigureAwait(false);
                }
                break;

            case DtmfActionResult.Escalate escalate:
                await _events.Writer.WriteAsync(
                    new StrategyEvent.EscalationRequested(escalate.Reason, DateTimeOffset.UtcNow),
                    ct).ConfigureAwait(false);
                if (_navigator!.CurrentStep is { } currentForEscalation)
                {
                    WorkflowState.MarkStepCompleted(currentForEscalation.Id);
                }
                break;

            case DtmfActionResult.Transfer transfer:
                await _events.Writer.WriteAsync(
                    new StrategyEvent.EscalationRequested(transfer.Reason ?? "Transfer requested", DateTimeOffset.UtcNow),
                    ct).ConfigureAwait(false);
                await _outbound.Writer.WriteAsync(
                    new OutboundDirective.TransferCall(
                        transfer.TargetIdentifier,
                        MapTransferKind(transfer.Kind),
                        DateTimeOffset.UtcNow,
                        transfer.Reason),
                    ct).ConfigureAwait(false);
                _navigator!.Complete();
                break;

            case DtmfActionResult.HangUp:
            case DtmfActionResult.Complete:
                _navigator!.Complete();
                break;
        }
    }

    private static TransferKind MapTransferKind(TransferKindHint hint) => hint switch
    {
        TransferKindHint.PhoneNumber => TransferKind.BlindToPhoneNumber,
        TransferKindHint.TeamsUser => TransferKind.BlindToTeamsUser,
        TransferKindHint.Consultative => TransferKind.Consultative,
        _ => TransferKind.BlindToPhoneNumber
    };
}

