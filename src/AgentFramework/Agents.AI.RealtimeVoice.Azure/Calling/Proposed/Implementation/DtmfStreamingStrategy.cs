using System.Threading.Channels;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.LiveVoice.Media.Audio;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Implementation;

/// <summary>
/// Tier 4 strategy: DTMF menu navigation driven directly by <see cref="RealtimeIvrWorkflowDefinition"/>.
/// Ports <see cref="Transports.DtmfIvrTransport"/> onto <see cref="IConversationStrategy"/>.
/// </summary>
public sealed class DtmfStreamingStrategy : IConversationStrategy
{
    private readonly RealtimeIvrWorkflowDefinition _workflow;
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

    private readonly CancellationTokenSource _cts = new();

    private Task? _runLoop;
    private string? _currentStepId;
    private string _digitBuffer = string.Empty;
    private bool _suspended;

    public DtmfStreamingStrategy(
        RealtimeIvrWorkflowDefinition workflow,
        ISpeechSynthesizer synthesizer,
        IvrWorkflowState? restoreFrom = null,
        ILoggerFactory? loggerFactory = null)
    {
        _workflow = workflow;
        _synthesizer = synthesizer;
        _logger = loggerFactory?.CreateLogger<DtmfStreamingStrategy>() ?? NullLogger<DtmfStreamingStrategy>.Instance;

        WorkflowState = new IvrWorkflowState { Status = IvrWorkflowStatus.Running };
        if (restoreFrom is not null)
        {
            WorkflowStateRestore.CopyInto(restoreFrom, WorkflowState);
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

        await _events.Writer.WriteAsync(
            new StrategyEvent.DtmfRecognized(digit.ToString(), _currentStepId, tone.Timestamp),
            ct).ConfigureAwait(false);

        if (step.DtmfMenuOptions is not null)
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
        if (step.DtmfMenuOptions is null || !step.DtmfMenuOptions.TryGetValue(digit, out var selectedOption))
        {
            await SpeakAsync("That is not a valid option. Please try again.", ct).ConfigureAwait(false);
            return;
        }

        WorkflowState.Set($"{_currentStepId}_selection", selectedOption);

        var transitions = step.ValidTransitions;
        var nextStep = transitions.FirstOrDefault(t => string.Equals(t, selectedOption, StringComparison.OrdinalIgnoreCase))
                       ?? (transitions.Count > 0 ? transitions[0] : null);

        if (nextStep is not null)
        {
            WorkflowState.MarkStepCompleted(_currentStepId!);
            _currentStepId = nextStep;
            WorkflowState.CurrentStepName = nextStep;
            await EnterStepAsync(nextStep, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessDigitCollectionAsync(RealtimeIvrWorkflowStep step, char digit, CancellationToken ct)
    {
        switch (digit)
        {
            case '#' when _digitBuffer.Length > 0:
                WorkflowState.Set(_currentStepId!, _digitBuffer);
                _digitBuffer = string.Empty;

                var transitions = step.ValidTransitions;
                if (transitions.Count > 0)
                {
                    WorkflowState.MarkStepCompleted(_currentStepId!);
                    _currentStepId = transitions[0];
                    WorkflowState.CurrentStepName = _currentStepId;
                    await EnterStepAsync(_currentStepId, ct).ConfigureAwait(false);
                }
                break;

            case '*':
                _digitBuffer = string.Empty;
                await SpeakAsync("Cleared. Please enter your digits again.", ct).ConfigureAwait(false);
                break;

            default:
                if (char.IsDigit(digit))
                {
                    _digitBuffer += digit;
                }
                break;
        }
    }

    private async Task EnterStepAsync(string? stepId, CancellationToken ct)
    {
        if (stepId is null)
        {
            return;
        }

        var step = _workflow.GetStep(stepId);
        if (step is null)
        {
            return;
        }

        await _events.Writer.WriteAsync(
            new StrategyEvent.WorkflowStepEntered(stepId, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        var prompt = BuildPrompt(step);
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            await SpeakAsync(prompt, ct).ConfigureAwait(false);
        }
    }

    private async Task SpeakAsync(string text, CancellationToken ct)
    {
        await _events.Writer.WriteAsync(
            new StrategyEvent.AgentUtterance("dtmf", text, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        try
        {
            await foreach (var pcm in _synthesizer.SynthesizeAsync(text, ct).ConfigureAwait(false))
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
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TTS synthesis failed for DTMF strategy");
        }
    }

    private static string BuildPrompt(RealtimeIvrWorkflowStep step)
    {
        var prompt = step.ConversationState.Description ?? step.ConversationState.Goal ?? string.Empty;

        if (step.DtmfMenuOptions is { Count: > 0 } menu)
        {
            var menuText = string.Join(". ", menu.Select(kv => $"Press {kv.Key} for {kv.Value}"));
            prompt = $"{prompt}. {menuText}.";
        }
        else if (!string.IsNullOrWhiteSpace(prompt))
        {
            prompt = $"{prompt}. Enter your response followed by the pound sign.";
        }

        return prompt;
    }
}
