using System.Threading.Channels;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Implementation;

/// <summary>
/// Verb-based companion to <see cref="DtmfStreamingStrategy"/>. Emits
/// <see cref="OutboundDirective.SpeakText"/> for prompts and
/// <see cref="OutboundDirective.CollectDtmf"/> for input collection. No local
/// speech synthesizer dependency — ACS does the rendering via its attached
/// Cognitive Services. Pair with <see cref="AcsCallAutomationEdge"/>.
/// </summary>
public sealed class DtmfVerbStrategy : IConversationStrategy
{
    private readonly RealtimeIvrWorkflowDefinition _workflow;
    private readonly ILogger _logger;

    private readonly Channel<OutboundDirective> _outbound = Channel.CreateBounded<OutboundDirective>(
        new BoundedChannelOptions(64)
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

    public DtmfVerbStrategy(
        RealtimeIvrWorkflowDefinition workflow,
        IvrWorkflowState? restoreFrom = null,
        ILoggerFactory? loggerFactory = null)
    {
        _workflow = workflow;
        _logger = loggerFactory?.CreateLogger<DtmfVerbStrategy>() ?? NullLogger<DtmfVerbStrategy>.Instance;

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

    public EdgeCapabilities EmittedDirectives =>
        EdgeCapabilities.SpeakText | EdgeCapabilities.CollectDtmf | EdgeCapabilities.StopPlayback;

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
            try { await _runLoop.ConfigureAwait(false); } catch { /* shutdown */ }
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
            _logger.LogError(ex, "DTMF verb strategy faulted for call {CallId}", context.CallId);
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
            new StrategyEvent.DtmfRecognized(tone.Digit.ToString(), _currentStepId, tone.Timestamp),
            ct).ConfigureAwait(false);

        if (step.DtmfMenuOptions is not null)
        {
            await ProcessMenuSelectionAsync(step, tone.Digit, ct).ConfigureAwait(false);
        }
        else
        {
            await ProcessDigitCollectionAsync(step, tone.Digit, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessMenuSelectionAsync(RealtimeIvrWorkflowStep step, char digit, CancellationToken ct)
    {
        if (step.DtmfMenuOptions is null || !step.DtmfMenuOptions.TryGetValue(digit, out var selectedOption))
        {
            await SpeakAsync("That is not a valid option. Please try again.", ct).ConfigureAwait(false);
            await RecognizeMenuAsync(step, ct).ConfigureAwait(false);
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

        if (step.DtmfMenuOptions is not null)
        {
            await RecognizeMenuAsync(step, ct).ConfigureAwait(false);
        }
        else if (step.ValidTransitions.Count > 0)
        {
            // Free-form digit collection terminated by '#'.
            await _outbound.Writer.WriteAsync(
                new OutboundDirective.CollectDtmf(
                    MaxTones: 16,
                    At: DateTimeOffset.UtcNow,
                    StopTone: '#',
                    OperationContext: stepId),
                ct).ConfigureAwait(false);
        }
    }

    private Task RecognizeMenuAsync(RealtimeIvrWorkflowStep step, CancellationToken ct)
        => _outbound.Writer.WriteAsync(
            new OutboundDirective.CollectDtmf(
                MaxTones: 1,
                At: DateTimeOffset.UtcNow,
                StopTone: null,
                OperationContext: _currentStepId),
            ct).AsTask();

    private async Task SpeakAsync(string text, CancellationToken ct)
    {
        await _events.Writer.WriteAsync(
            new StrategyEvent.AgentUtterance("dtmf-verb", text, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        if (_suspended)
        {
            return;
        }
        await _outbound.Writer.WriteAsync(
            new OutboundDirective.SpeakText(text, DateTimeOffset.UtcNow, OperationContext: _currentStepId),
            ct).ConfigureAwait(false);
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

public sealed class DtmfVerbStrategyFactory : IConversationStrategyFactory
{
    public AgentTier Tier => AgentTier.DtmfOnly;

    public ValueTask<IConversationStrategy> CreateAsync(
        string callId,
        IServiceProvider services,
        RealtimeIvrWorkflowDefinition workflow,
        IvrWorkflowState? restoreFrom,
        CancellationToken cancellationToken = default)
    {
        var loggerFactory = services.GetService(typeof(Microsoft.Extensions.Logging.ILoggerFactory)) as ILoggerFactory;
        IConversationStrategy strategy = new DtmfVerbStrategy(workflow, restoreFrom, loggerFactory);
        return ValueTask.FromResult(strategy);
    }
}
