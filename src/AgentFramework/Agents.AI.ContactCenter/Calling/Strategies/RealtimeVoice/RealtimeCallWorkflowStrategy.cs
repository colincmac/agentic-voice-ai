using System.Threading.Channels;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.IvrWorkflow.Navigation;
using Agents.AI.ContactCenter.Telemetry;
using Agents.AI.Extensions.AITools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Calling.Strategies.RealtimeVoice;

/// <summary>
/// Phase-5 successor to <see cref="RealtimeVoiceStrategy"/> built on the new
/// <see cref="CompiledCallWorkflow"/> + <see cref="WorkflowExecutor"/> + single-advance
/// model. Wraps an <see cref="IRealtimeVoiceBackend"/> and:
/// <list type="bullet">
///   <item>Resolves stage-scoped tools through <see cref="INamedAIFunctionProvider"/>.</item>
///   <item>Renders stage prompts through <see cref="StagePromptRenderer"/>.</item>
///   <item>Synthesizes a single <see cref="AdvanceFunctionBuilder.FunctionName"/> per stage instead of N <c>advance_to_*</c> tools.</item>
///   <item>Handles inbound DTMF by checking the current stage's scripted menu, falling back to a user-text turn into the model.</item>
/// </list>
/// </summary>
/// <remarks>
/// Lives alongside the legacy <see cref="RealtimeVoiceStrategy"/> while strategies and tests
/// migrate. The legacy strategy is unchanged by Phase 5; it goes away in a later phase.
/// </remarks>
public sealed class RealtimeCallWorkflowStrategy : IConversationStrategy
{
    private readonly IRealtimeVoiceBackend _backend;
    private readonly CallWorkflowSession _session;
    private readonly INamedAIFunctionProvider _toolProvider;
    private readonly WorkflowExecutor _executor;
    private readonly CallingTelemetry _telemetry;
    private readonly ILogger _logger;
    private string _callId = string.Empty;

    private readonly Channel<OutboundDirective> _outbound = Channel.CreateBounded<OutboundDirective>(
        new BoundedChannelOptions(500)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    private readonly Channel<StrategyEvent> _events = Channel.CreateUnbounded<StrategyEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource _cts = new();
    private Task? _agentLoop;
    private Task? _audioPump;
    private Task? _dtmfPump;
    private bool _suspended;
    private bool _prewarmed;

    public RealtimeCallWorkflowStrategy(
        IRealtimeVoiceBackend backend,
        CallWorkflowSession session,
        INamedAIFunctionProvider toolProvider,
        CallingTelemetry telemetry,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(toolProvider);
        ArgumentNullException.ThrowIfNull(telemetry);

        _backend = backend;
        _session = session;
        _toolProvider = toolProvider;
        _telemetry = telemetry;
        _logger = loggerFactory?.CreateLogger<RealtimeCallWorkflowStrategy>()
            ?? NullLogger<RealtimeCallWorkflowStrategy>.Instance;

        _executor = new WorkflowExecutor(_session, RenderStageAsync);
    }

    public StrategyKind Kind => StrategyKind.RealtimeVoice;

    public AgentTier Tier => AgentTier.RealtimeVoice;

    public IvrWorkflowState WorkflowState => _session.State;

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

        if (!_prewarmed)
        {
            await ConnectBackendAsync(cancellationToken).ConfigureAwait(false);
        }

        await _executor.EnterAsync(cancellationToken).ConfigureAwait(false);

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
        await ConnectBackendAsync(cancellationToken).ConfigureAwait(false);
        _prewarmed = true;
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

    private async Task ConnectBackendAsync(CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// Render <paramref name="stage"/>'s prompt + tool surface onto the realtime backend.
    /// Invoked by <see cref="WorkflowExecutor"/> on initial entry and after every
    /// successful transition.
    /// </summary>
    private async ValueTask RenderStageAsync(CompiledStage stage, CancellationToken cancellationToken)
    {
        var prompt = StagePromptRenderer.RenderRealtimePrompt(_session.Workflow, stage, _session.State);
        var tools = ResolveStageTools(stage);

        await _backend.StartResponseAsync(tools, prompt, cancellationToken).ConfigureAwait(false);

        await _events.Writer.WriteAsync(
            new StrategyEvent.WorkflowStepEntered(stage.Id, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        await _events.Writer.WriteAsync(
            new StrategyEvent.AgentSpeakingChanged(_backend.AgentId, _backend.AgentDisplayName, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        if (_session.State.IsComplete)
        {
            await EndSessionAsync($"terminal stage '{stage.Id}' reached", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolve the tool surface for <paramref name="stage"/>: workflow common tools +
    /// stage-scoped tools + stage-scoped realtime tool overrides + the synthesized
    /// <c>advance</c> function.
    /// </summary>
    private List<AITool> ResolveStageTools(CompiledStage stage)
    {
        var names = new List<string>();
        names.AddRange(_session.Workflow.Blueprint.CommonToolNames);
        names.AddRange(stage.Blueprint.ToolNames);
        if (stage.Blueprint.Channels.Realtime is { ToolNames.Count: > 0 } realtime)
        {
            names.AddRange(realtime.ToolNames);
        }

        // De-dupe by name preserving order — last-wins behavior is up to the provider.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>(names.Count);
        foreach (var name in names)
        {
            if (!string.IsNullOrEmpty(name) && seen.Add(name))
            {
                ordered.Add(name);
            }
        }

        var resolved = _toolProvider.ResolveAll(ordered).Cast<AITool>().ToList();

        if (AdvanceFunctionBuilder.BuildForStage(stage, _executor) is { } advanceFn)
        {
            resolved.Add(advanceFn);
        }

        return resolved;
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

    /// <summary>
    /// Per-stage DTMF handling. For each tone:
    /// <list type="number">
    ///   <item>Emit <c>DtmfRecognized</c> for observability.</item>
    ///   <item>If the current stage has a scripted menu and the digit matches, advance via the mapped transition label.</item>
    ///   <item>Otherwise forward the digit to the realtime backend as an inline user text turn.</item>
    /// </list>
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

                var current = _executor.Navigator.CurrentStage;
                await _events.Writer.WriteAsync(
                    new StrategyEvent.DtmfRecognized(tone.Digit.ToString(), current?.Id, DateTimeOffset.UtcNow),
                    ct).ConfigureAwait(false);

                if (current?.Blueprint.Channels.Scripted is { MenuOptions: { Count: > 0 } menu }
                    && menu.TryGetValue(tone.Digit, out var option))
                {
                    var edge = current.FindEdgeByLabel(option.TransitionLabel);
                    if (edge is null)
                    {
                        _logger.LogWarning(
                            "Stage '{Stage}' DTMF menu maps digit '{Digit}' to label '{Label}', but no outgoing edge matches.",
                            current.Id, tone.Digit, option.TransitionLabel);
                        await ForwardDtmfAsTextAsync(tone, ct).ConfigureAwait(false);
                        continue;
                    }

                    var outcome = await _executor.AdvanceAlongAsync(edge, ct).ConfigureAwait(false);
                    if (outcome is AdvanceOutcome.Denied or AdvanceOutcome.Invalid)
                    {
                        var reason = outcome switch
                        {
                            AdvanceOutcome.Denied d => d.Reason,
                            AdvanceOutcome.Invalid i => i.Reason,
                            _ => "unknown",
                        };
                        await SendBackendNoteAsync($"[Caller pressed {tone.Digit}; transition denied: {reason}]", ct).ConfigureAwait(false);
                    }
                    continue;
                }

                await ForwardDtmfAsTextAsync(tone, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Realtime inbound DTMF pump terminated for call {CallId}", _callId);
        }
    }

    private Task ForwardDtmfAsTextAsync(DtmfTone tone, CancellationToken ct) =>
        SendBackendNoteAsync($"[Caller pressed {tone.Digit}]", ct);

    private async Task SendBackendNoteAsync(string note, CancellationToken ct)
    {
        try
        {
            await _backend.SendUserTextAsync(note, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to surface inline note to backend: {Note}", note);
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
                        break;

                    case RealtimeBackendUpdate.UserSpeechStarted speech when !_suspended:
                        await _outbound.Writer.WriteAsync(
                            new OutboundDirective.StopPlayback(speech.At),
                            ct).ConfigureAwait(false);
                        break;

                    case RealtimeBackendUpdate.Faulted fault:
                        _logger.LogWarning(fault.Exception, "Realtime backend faulted: {Message}", fault.Message);
                        await _events.Writer.WriteAsync(
                            new StrategyEvent.Faulted(fault.Message, fault.Exception, fault.At),
                            CancellationToken.None).ConfigureAwait(false);
                        return;
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

    private async Task EndSessionAsync(string reason, CancellationToken ct)
    {
        await _events.Writer.WriteAsync(
            new StrategyEvent.AgentUtterance(_backend.AgentId, $"[session ending: {reason}]", DateTimeOffset.UtcNow),
            CancellationToken.None).ConfigureAwait(false);
        await _cts.CancelAsync().ConfigureAwait(false);
    }
}
