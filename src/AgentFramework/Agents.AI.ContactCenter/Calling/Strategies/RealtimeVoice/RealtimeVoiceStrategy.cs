using System.Diagnostics;
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
    private bool _suspended;
    private bool _prewarmed;
    private CallEdgeMetadata? _callerMetadata;
    private ConversationContext? _conversationContext;

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
        await ApplyStageAsync(step, cancellationToken).ConfigureAwait(false);
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

        await _backend.UpdateToolsAsync(tools, cancellationToken).ConfigureAwait(false);

        var prompt = _navigator.BuildCurrentStepPrompt(_conversationContext);
        await _backend.UpdateSystemPromptAsync(prompt, cancellationToken).ConfigureAwait(false);

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

    //private async Task PumpInboundDtmfAsync(StrategyStartContext context, CancellationToken ct)
    //{
    //    try
    //    {
    //        await foreach (var frame in context.InboundDtmf.ReadAllAsync(ct).ConfigureAwait(false))
    //        {
    //            if (_suspended)
    //            {
    //                continue;
    //            }
    //            await _backend.SendDtmfAsync(frame, ct).ConfigureAwait(false);
    //        }
    //    }
    //    catch (OperationCanceledException) { /* shutdown */ }
    //    catch (Exception ex)
    //    {
    //        _logger.LogWarning(ex, "Realtime inbound audio pump terminated");
    //    }
    //}

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

        await ApplyStageAsync(result.NewStep, ct).ConfigureAwait(false);

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
