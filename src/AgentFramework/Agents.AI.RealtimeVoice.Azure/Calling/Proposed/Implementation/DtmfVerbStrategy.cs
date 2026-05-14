using System.Threading.Channels;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Authentication;
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
    private readonly ILoggerFactory? _loggerFactory;
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
    private IIvrWorkflowNavigator? _navigator;
    private string _digitBuffer = string.Empty;
    private bool _suspended;
    private bool _prewarmed;
    private RealtimeIvrWorkflowStep? _prewarmedInitialStep;
    private List<OutboundDirective>? _prewarmedInitialDirectives;
    private CallEdgeMetadata? _callerMetadata;
    private string _callId = string.Empty;

    public DtmfVerbStrategy(
        RealtimeIvrWorkflowDefinition workflow,
        IvrWorkflowState? restoreFrom = null,
        ILoggerFactory? loggerFactory = null)
    {
        _workflow = workflow;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<DtmfVerbStrategy>() ?? NullLogger<DtmfVerbStrategy>.Instance;

        WorkflowState = restoreFrom ?? new IvrWorkflowState { Status = IvrWorkflowStatus.Running };
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
        _prewarmedInitialDirectives = BuildEnterStepDirectives(initial, out var prompt);

        await _events.Writer.WriteAsync(
            new StrategyEvent.WorkflowStepEntered(initial.Id, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            await _events.Writer.WriteAsync(
                new StrategyEvent.AgentUtterance("dtmf-verb", prompt, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }

        _prewarmed = true;
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
            // Authenticate the caller before walking the workflow so transitions / validators
            // that depend on WorkflowState.AuthLevel see the correct value.
            await CallerAuthenticationRunner.RunAsync(
                context.Services,
                _callId,
                _callerMetadata,
                _events.Writer,
                WorkflowState,
                telemetry: null,
                logger: _logger,
                cancellationToken: ct).ConfigureAwait(false);

            if (_prewarmed && _prewarmedInitialDirectives is { Count: > 0 } buffered)
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
        var step = _navigator?.CurrentStep;
        if (step is null)
        {
            return;
        }

        await _events.Writer.WriteAsync(
            new StrategyEvent.DtmfRecognized(tone.Digit.ToString(), step.Id, tone.Timestamp),
            ct).ConfigureAwait(false);

        if (step.StepDtmfConfiguration?.MenuOptions is not null)
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
        if (!_navigator!.TryResolveDtmfDigit(digit, out var option))
        {
            await SpeakAsync("That is not a valid option. Please try again.", ct).ConfigureAwait(false);
            await RecognizeMenuAsync(step, ct).ConfigureAwait(false);
            return;
        }

        WorkflowState.Set($"{step.Id}_selection", option.Label);

        var actionResult = await _navigator.InvokeMenuActionAsync(option, extraArguments: null, ct).ConfigureAwait(false);
        await DispatchAsync(actionResult, step, ct).ConfigureAwait(false);
    }

    private async Task ProcessDigitCollectionAsync(RealtimeIvrWorkflowStep step, char digit, CancellationToken ct)
    {
        switch (digit)
        {
            case '#' when _digitBuffer.Length > 0:
                WorkflowState.Set(step.Id, _digitBuffer);
                _digitBuffer = string.Empty;

                var transitions = step.ValidTransitions;
                if (transitions.Count > 0)
                {
                    await DispatchAsync(new DtmfActionResult.Transition(transitions[0]), step, ct).ConfigureAwait(false);
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
                if (!string.IsNullOrEmpty(repeat.Prompt))
                {
                    await SpeakAsync(repeat.Prompt, ct).ConfigureAwait(false);
                }
                else
                {
                    await SpeakAsync(BuildPrompt(step), ct).ConfigureAwait(false);
                }
                if (step.StepDtmfConfiguration?.MenuOptions is not null)
                {
                    await RecognizeMenuAsync(step, ct).ConfigureAwait(false);
                }
                break;

            case DtmfActionResult.Reject reject:
                var errorPrompt = reject.ErrorPrompt
                    ?? step.StepDtmfConfiguration?.OnErrorPrompt
                    ?? "That is not a valid option. Please try again.";
                await SpeakAsync(errorPrompt, ct).ConfigureAwait(false);
                if (step.StepDtmfConfiguration?.MenuOptions is not null)
                {
                    await RecognizeMenuAsync(step, ct).ConfigureAwait(false);
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

    private async Task EnterStepAsync(RealtimeIvrWorkflowStep step, CancellationToken ct)
    {
        await _events.Writer.WriteAsync(
            new StrategyEvent.WorkflowStepEntered(step.Id, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        var directives = BuildEnterStepDirectives(step, out var prompt);

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            await _events.Writer.WriteAsync(
                new StrategyEvent.AgentUtterance("dtmf-verb", prompt, DateTimeOffset.UtcNow),
                ct).ConfigureAwait(false);
        }

        foreach (var directive in directives)
        {
            if (_suspended)
            {
                break;
            }
            await _outbound.Writer.WriteAsync(directive, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Render a step into the speak + recognize directives that drive the verb-mode caller edge.
    /// Returns the prompt text (used for the AgentUtterance event) via <paramref name="prompt"/>.
    /// Used by both the live <see cref="EnterStepAsync"/> path and <see cref="PrewarmAsync"/>.
    /// </summary>
    private static List<OutboundDirective> BuildEnterStepDirectives(RealtimeIvrWorkflowStep step, out string prompt)
    {
        var directives = new List<OutboundDirective>();
        prompt = BuildPrompt(step);

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            directives.Add(new OutboundDirective.SpeakText(prompt, DateTimeOffset.UtcNow, OperationContext: step.Id));
        }

        if (step.StepDtmfConfiguration?.MenuOptions is not null)
        {
            directives.Add(new OutboundDirective.CollectDtmf(
                MaxTones: 1,
                At: DateTimeOffset.UtcNow,
                StopTone: null,
                OperationContext: step.Id));
        }
        else if (step.ValidTransitions.Count > 0)
        {
            directives.Add(new OutboundDirective.CollectDtmf(
                MaxTones: 16,
                At: DateTimeOffset.UtcNow,
                StopTone: '#',
                OperationContext: step.Id));
        }

        return directives;
    }

    private Task RecognizeMenuAsync(RealtimeIvrWorkflowStep step, CancellationToken ct)
        => _outbound.Writer.WriteAsync(
            new OutboundDirective.CollectDtmf(
                MaxTones: 1,
                At: DateTimeOffset.UtcNow,
                StopTone: null,
                OperationContext: step.Id),
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
            new OutboundDirective.SpeakText(text, DateTimeOffset.UtcNow, OperationContext: WorkflowState.CurrentStepName),
            ct).ConfigureAwait(false);
    }

    private static string BuildPrompt(RealtimeIvrWorkflowStep step)
    {
        var prompt = step.ConversationState.Description ?? step.ConversationState.Goal ?? string.Empty;

        if (step.StepDtmfConfiguration?.MenuOptions is { Count: > 0 } menu)
        {
            var menuText = string.Join(". ", menu.Select(kv => $"Press {kv.Key} for {kv.Value.Label}"));
            prompt = $"{prompt}. {menuText}.";
        }
        else if (!string.IsNullOrWhiteSpace(prompt))
        {
            prompt = $"{prompt}. Enter your response followed by the pound sign.";
        }

        return prompt;
    }
}

