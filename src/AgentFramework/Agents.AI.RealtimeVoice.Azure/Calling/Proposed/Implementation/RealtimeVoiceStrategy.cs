using System.Threading.Channels;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Implementation;

/// <summary>
/// Tier 0 strategy: native realtime audio-to-audio model. Ports
/// <see cref="Transports.RealtimeVoiceAgentTransport"/> onto the new
/// <see cref="IConversationStrategy"/> contract via <see cref="IRealtimeVoiceBackend"/>.
/// </summary>
public sealed class RealtimeVoiceStrategy : IConversationStrategy
{
    private readonly IRealtimeVoiceBackend _backend;
    private readonly RealtimeIvrWorkflowDefinition _workflow;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger _logger;

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

    public RealtimeVoiceStrategy(
        IRealtimeVoiceBackend backend,
        RealtimeIvrWorkflowDefinition workflow,
        IvrWorkflowState? restoreFrom = null,
        ILoggerFactory? loggerFactory = null)
    {
        _backend = backend;
        _workflow = workflow;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<RealtimeVoiceStrategy>() ?? NullLogger<RealtimeVoiceStrategy>.Instance;

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

        _navigator = new IvrWorkflowNavigator(
            _workflow,
            WorkflowState,
            context.Services,
            _loggerFactory?.CreateLogger<IvrWorkflowNavigator>());

        await _backend.ConnectAsync(cancellationToken).ConfigureAwait(false);

        // Seed the agent with the system prompt for the current workflow step.
        var step = _navigator.EnterInitialStep();

        // Push the step's tool surface, wrapped with the step's guards so any tool
        // invocation by the realtime model is gated by the navigator's live state.
        var initialTools = _navigator.WrapToolsWithCurrentGuards(step.AvailableTools ?? Array.Empty<AITool>());
        await _backend.UpdateToolsAsync(initialTools, cancellationToken).ConfigureAwait(false);

        var prompt = _navigator.BuildCurrentStepPrompt();
        await _backend.UpdateSystemPromptAsync(prompt, cancellationToken).ConfigureAwait(false);
        await _events.Writer.WriteAsync(
            new StrategyEvent.WorkflowStepEntered(step.Id, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        await _events.Writer.WriteAsync(
            new StrategyEvent.AgentSpeakingChanged(_backend.AgentId, _backend.AgentDisplayName, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _audioPump = Task.Run(() => PumpInboundAudioAsync(context, linked.Token), CancellationToken.None);
        _agentLoop = Task.Run(() => RunAgentLoopAsync(linked.Token), CancellationToken.None);
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
            await _events.Writer.WriteAsync(
                new StrategyEvent.Faulted(ex.Message, ex, DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
        }
    }
}
