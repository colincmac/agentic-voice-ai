using System.Text;
using System.Threading.Channels;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.LiveVoice.Media.Audio;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Implementation;

/// <summary>
/// Tier 4 strategy: DTMF menu navigation driven directly by <see cref="RealtimeIvrWorkflowDefinition"/>.
/// Ports <see cref="Transports.DtmfIvrTransport"/> onto <see cref="IConversationStrategy"/>.
/// </summary>
public sealed class DtmfStreamingStrategy : IConversationStrategy
{
    private readonly RealtimeIvrWorkflowDefinition _workflow;
    private readonly ISpeechSynthesizer? _synthesizer;
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
    private string? _currentStepId;
    private readonly StringBuilder _digitBuffer = new();
    private IServiceProvider? _services;

    private bool _suspended;
    private DateTime _lastDtmfReceivedTime = DateTime.MinValue;
    private CancellationTokenSource? _interDigitTimeoutCts;
    private int _interDigitTimeoutMs = 5000;
    private readonly char _terminationDigitChar = '#';
    private readonly Lock _stateLock = new();

    public DtmfStreamingStrategy(
        RealtimeIvrWorkflowDefinition workflow,
        ISpeechSynthesizer? synthesizer = null,
        IvrWorkflowState? restoreFrom = null,
        ILoggerFactory? loggerFactory = null)
    {
        _workflow = workflow;
        _synthesizer = synthesizer;
        _logger = loggerFactory?.CreateLogger<DtmfStreamingStrategy>() ?? NullLogger<DtmfStreamingStrategy>.Instance;

        WorkflowState = new IvrWorkflowState { Status = IvrWorkflowStatus.Running };
        if (restoreFrom is not null)
        {
            WorkflowStateExtensions.CopyInto(restoreFrom, WorkflowState);
        }

        _currentStepId = WorkflowState.CurrentStepName ?? workflow.InitialStepId;
        WorkflowState.CurrentStepName = _currentStepId;
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

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _runLoop = Task.Run(() => RunAsync(context, linked.Token), CancellationToken.None);
        return Task.CompletedTask;
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
        _services = context.Services;
        try
        {
            await EnterStepAsync(_currentStepId, ct).ConfigureAwait(false);

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
        var stepId = _currentStepId;
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
        if (_currentStepId is null)
        {
            return;
        }

        var step = _workflow.GetStep(_currentStepId);
        if (step is null)
        {
            return;
        }

        await PublishDtmfRecognizedAsync(digit.ToString(), ct).ConfigureAwait(false);

        var dtmf = step.StepDtmfConfiguration;
        var hasMenu = (dtmf?.Options is { Count: > 0 }) || (dtmf?.MenuOptions is { Count: > 0 });

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
        var dtmf = step.StepDtmfConfiguration;
        if (dtmf is null)
        {
            return;
        }

        // Prefer rich menu binding when present.
        if (dtmf.MenuOptions is { } menuOptions && menuOptions.TryGetValue(digit, out var option))
        {
            WorkflowState.Set($"{_currentStepId}_selection", option.Label);

            if (option.Action is null)
            {
                // Declarative option: jump directly to NextStepId (or fall back to legacy
                // behaviour of matching the label to a valid transition).
                var target = option.NextStepId ?? ResolveLegacyTransitionTarget(step, option.Label);
                if (target is not null)
                {
                    await DispatchAsync(new DtmfActionResult.Transition(target), step, ct).ConfigureAwait(false);
                }
                return;
            }

            var actionResult = await InvokeActionAsync(
                option.Action,
                option.Arguments,
                extraArgs: null,
                successNextStepId: option.NextStepId,
                failurePrompt: option.OnFailurePrompt,
                failureAudio: option.OnFailureAudioFile,
                ct).ConfigureAwait(false);

            await DispatchAsync(actionResult, step, ct).ConfigureAwait(false);
            return;
        }

        // Legacy path: simple Dictionary<char, string> menu.
        if (dtmf.Options is null || !dtmf.Options.TryGetValue(digit, out var selectedOption))
        {
            await SpeakAsync(step, ct).ConfigureAwait(false);
            return;
        }

        WorkflowState.Set($"{_currentStepId}_selection", selectedOption);

        var nextStep = ResolveLegacyTransitionTarget(step, selectedOption);
        if (nextStep is not null)
        {
            await DispatchAsync(new DtmfActionResult.Transition(nextStep), step, ct).ConfigureAwait(false);
        }
    }

    private static string? ResolveLegacyTransitionTarget(RealtimeIvrWorkflowStep step, string label)
    {
        var transitions = step.ValidTransitions;
        return transitions.FirstOrDefault(t => string.Equals(t, label, StringComparison.OrdinalIgnoreCase))
               ?? (transitions.Count > 0 ? transitions[0] : null);
    }

    private async Task ProcessDigitCollectionAsync(RealtimeIvrWorkflowStep step, char digit, CancellationToken ct)
    {
        var dtmf = step.StepDtmfConfiguration;
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

            var stateKey = dtmf.CollectedStateKey ?? $"{_currentStepId}_collected";
            var extra = new Dictionary<string, object?>
            {
                [dtmf.DigitsParameterName] = collected,
            };

            var actionResult = await InvokeActionAsync(
                validator,
                dtmf.DigitCollectionArguments,
                extraArgs: extra,
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

        // Legacy path: store under the default key and walk the first transition.
        WorkflowState.Set($"{_currentStepId}_collected", collected);
        var transitions = step.ValidTransitions;
        if (transitions.Count > 0)
        {
            await DispatchAsync(new DtmfActionResult.Transition(transitions[0]), step, ct).ConfigureAwait(false);
        }
    }

    private async Task EnterStepAsync(string? stepId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(stepId) || _workflow.GetStep(stepId) is not { } step)
        {
            return;
        }

        await _events.Writer.WriteAsync(
            new StrategyEvent.WorkflowStepEntered(stepId, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        await SpeakAsync(step, ct).ConfigureAwait(false);
    }

    private async Task SpeakAsync(RealtimeIvrWorkflowStep step, CancellationToken ct)
    {
        try
        {
            if (_synthesizer is not null)
            {
                var (prompt, format) = BuildPrompt(step);

                await foreach (var pcm in _synthesizer.SynthesizeAsync(prompt, format, ct).ConfigureAwait(false))
                {
                    if (_suspended)
                    {
                        break;
                    }
                    await _outbound.Writer.WriteAsync(
                        new OutboundDirective.Audio(
                            new AudioFrame(pcm, DateTimeOffset.UtcNow, SourceEdgeId: null)),
                        ct).ConfigureAwait(false);
                }
                await _events.Writer.WriteAsync(
                    new StrategyEvent.AgentUtterance("dtmf", prompt, DateTimeOffset.UtcNow),
                    ct).ConfigureAwait(false);
            }
            else
            {
                if(step.StepDtmfConfiguration?.AudioFile is Uri fileUri)
                {
                    await _outbound.Writer.WriteAsync(
                        new OutboundDirective.PlayFile(FileUri: fileUri, DateTimeOffset.UtcNow),
                        ct).ConfigureAwait(false);
                }
                else if(!string.IsNullOrEmpty(step.StepDtmfConfiguration?.PromptOverride))
                {
                    await _outbound.Writer.WriteAsync(
                        new OutboundDirective.SpeakText(step.StepDtmfConfiguration.PromptOverride, DateTimeOffset.UtcNow),
                        ct).ConfigureAwait(false);
                }
            }

        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TTS synthesis failed for DTMF strategy");
        }
    }

    private static (string prompt, SynthesizerInputFormat format) BuildPrompt(RealtimeIvrWorkflowStep step)
    {
        if(step.StepDtmfConfiguration?.PromptOverride is not null)
        {
            return (step.StepDtmfConfiguration.PromptOverride, SynthesizerInputFormat.SSML);
        }

        var prompt = new StringBuilder(step.ConversationState.Description ?? step.ConversationState.Goal ?? string.Empty);

        if (step.StepDtmfConfiguration?.Options is { Count: > 0 } menu)
        {
            var menuText = string.Join(". ", menu.Select(kv => $"Press {kv.Key} for {kv.Value}"));
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

    private async Task<DtmfActionResult> InvokeActionAsync(
        AITool action,
        IReadOnlyDictionary<string, object?>? boundArgs,
        IReadOnlyDictionary<string, object?>? extraArgs,
        string? successNextStepId,
        string? failurePrompt,
        Uri? failureAudio,
        CancellationToken ct)
    {
        if (action is not AIFunction fn)
        {
            _logger.LogWarning(
                "DTMF action tool '{Name}' is not an AIFunction and cannot be invoked.",
                action.Name);
            return new DtmfActionResult.Reject(failurePrompt, failureAudio);
        }

        var args = new AIFunctionArguments
        {
            Services = _services,
        };

        if (boundArgs is not null)
        {
            foreach (var kv in boundArgs)
            {
                args[kv.Key] = kv.Value;
            }
        }

        if (extraArgs is not null)
        {
            foreach (var kv in extraArgs)
            {
                args[kv.Key] = kv.Value;
            }
        }

        try
        {
            var raw = await fn.InvokeAsync(args, ct).ConfigureAwait(false);
            return InterpretResult(raw, successNextStepId, failurePrompt, failureAudio);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "DTMF action tool '{Name}' threw; treating as rejection.",
                action.Name);
            return new DtmfActionResult.Reject(failurePrompt, failureAudio);
        }
    }

    private static DtmfActionResult InterpretResult(
        object? raw,
        string? successNextStepId,
        string? failurePrompt,
        Uri? failureAudio)
    {
        switch (raw)
        {
            case null:
                return successNextStepId is not null
                    ? new DtmfActionResult.Transition(successNextStepId)
                    : new DtmfActionResult.Repeat();
            case DtmfActionResult typed:
                return typed;
        }

        // Reflection fallback for envelopes like CallControlResult { bool Success; string Message; }.
        var type = raw.GetType();
        var successProp = type.GetProperty("Success") ?? type.GetProperty("IsSuccess");
        if (successProp is not null && successProp.PropertyType == typeof(bool))
        {
            var success = (bool)(successProp.GetValue(raw) ?? false);
            if (!success)
            {
                return new DtmfActionResult.Reject(failurePrompt, failureAudio);
            }
        }

        return successNextStepId is not null
            ? new DtmfActionResult.Transition(successNextStepId)
            : new DtmfActionResult.Repeat();
    }

    private async Task DispatchAsync(DtmfActionResult result, RealtimeIvrWorkflowStep step, CancellationToken ct)
    {
        switch (result)
        {
            case DtmfActionResult.Transition transition:
                if (_workflow.GetStep(transition.NextStepId) is null)
                {
                    _logger.LogWarning(
                        "DTMF action requested transition to unknown step '{StepId}'.",
                        transition.NextStepId);
                    return;
                }
                if (_currentStepId is not null)
                {
                    WorkflowState.MarkStepCompleted(_currentStepId);
                }
                _currentStepId = transition.NextStepId;
                WorkflowState.CurrentStepName = transition.NextStepId;
                await EnterStepAsync(transition.NextStepId, ct).ConfigureAwait(false);
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
                else if (step.StepDtmfConfiguration?.OnErrorAudioFile is { } stepErrAudio)
                {
                    await _outbound.Writer.WriteAsync(
                        new OutboundDirective.PlayFile(stepErrAudio, DateTimeOffset.UtcNow),
                        ct).ConfigureAwait(false);
                }
                else if (!string.IsNullOrEmpty(step.StepDtmfConfiguration?.OnErrorPrompt))
                {
                    await _outbound.Writer.WriteAsync(
                        new OutboundDirective.SpeakText(step.StepDtmfConfiguration!.OnErrorPrompt!, DateTimeOffset.UtcNow),
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
                if (_currentStepId is not null)
                {
                    WorkflowState.MarkStepCompleted(_currentStepId);
                }
                break;

            case DtmfActionResult.HangUp:
            case DtmfActionResult.Complete:
                if (_currentStepId is not null)
                {
                    WorkflowState.MarkStepCompleted(_currentStepId);
                }
                WorkflowState.Status = IvrWorkflowStatus.Completed;
                break;
        }
    }
}

