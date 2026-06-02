using System.Threading.Channels;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.Media.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Calling.Strategies.Dtmf;

/// <summary>
/// Phase-5 successor to <see cref="DtmfStreamingStrategy"/> built on the new
/// <see cref="CompiledCallWorkflow"/> + <see cref="WorkflowExecutor"/> model. Synthesizes
/// the current stage's SSML prompt (when configured), routes inbound DTMF tones via the
/// stage's scripted menu, and falls back to ignoring unmapped digits.
/// </summary>
/// <remarks>
/// Designed to run as the bottom tier of a <see cref="Composite.CompositeFallbackStrategy"/>;
/// preserves the per-call <see cref="IvrWorkflowState"/> across swaps because it's
/// supplied by the <see cref="CallWorkflowSession"/> created with <c>restoreFrom</c>.
/// </remarks>
public sealed class DtmfCallWorkflowStrategy : IConversationStrategy
{
    private readonly CallWorkflowSession _session;
    private readonly ISpeechSynthesizer? _synthesizer;
    private readonly WorkflowExecutor _executor;
    private readonly ILogger _logger;

    private readonly Channel<OutboundDirective> _outbound = Channel.CreateBounded<OutboundDirective>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private readonly Channel<StrategyEvent> _events = Channel.CreateUnbounded<StrategyEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource _cts = new();
    private Task? _dtmfPump;
    private bool _suspended;
    private string _callId = string.Empty;

    public DtmfCallWorkflowStrategy(
        CallWorkflowSession session,
        ISpeechSynthesizer? synthesizer = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _synthesizer = synthesizer;
        _logger = loggerFactory?.CreateLogger<DtmfCallWorkflowStrategy>()
            ?? NullLogger<DtmfCallWorkflowStrategy>.Instance;

        _executor = new WorkflowExecutor(_session, RenderStageAsync);
    }

    public StrategyKind Kind => StrategyKind.Dtmf;

    public AgentTier Tier => AgentTier.DtmfOnly;

    public IvrWorkflowState WorkflowState => _session.State;

    public EdgeCapabilities EmittedDirectives => EdgeCapabilities.Audio | EdgeCapabilities.StopPlayback;

    public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;

    public ChannelReader<StrategyEvent> Events => _events.Reader;

    public Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default)
    {
        if (_dtmfPump is not null) { return Task.CompletedTask; }

        _callId = context.CallId;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);

        _dtmfPump = Task.Run(async () =>
        {
            try
            {
                await _executor.EnterAsync(linked.Token).ConfigureAwait(false);

                await foreach (var tone in context.InboundDtmf.ReadAllAsync(linked.Token).ConfigureAwait(false))
                {
                    if (_suspended) { continue; }
                    await HandleDtmfAsync(tone, linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DTMF strategy faulted for call {CallId}", _callId);
                await _events.Writer.WriteAsync(
                    new StrategyEvent.Faulted(ex.Message, ex, DateTimeOffset.UtcNow),
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _outbound.Writer.TryComplete();
                _events.Writer.TryComplete();
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_dtmfPump is not null) { try { await _dtmfPump.ConfigureAwait(false); } catch { } }
        _outbound.Writer.TryComplete();
        _events.Writer.TryComplete();
    }

    public ValueTask SuspendAsync(CancellationToken cancellationToken = default) { _suspended = true; return ValueTask.CompletedTask; }
    public ValueTask ResumeAsync(CancellationToken cancellationToken = default) { _suspended = false; return ValueTask.CompletedTask; }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private async ValueTask RenderStageAsync(CompiledStage stage, CancellationToken ct)
    {
        await _events.Writer.WriteAsync(
            new StrategyEvent.WorkflowStepEntered(stage.Id, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        var scripted = stage.Blueprint.Channels.Scripted;
        if (scripted is null || _synthesizer is null)
        {
            return;
        }

        var ssml = scripted.SsmlPrompt;
        if (string.IsNullOrWhiteSpace(ssml)) { return; }

        var format = LooksLikeSsml(ssml) ? SynthesizerInputFormat.SSML : SynthesizerInputFormat.Text;

        try
        {
            await foreach (var pcm in _synthesizer.SynthesizeAsync(ssml, format, ct).ConfigureAwait(false))
            {
                if (_suspended) { break; }
                await _outbound.Writer.WriteAsync(
                    new OutboundDirective.Audio(new AudioFrame(pcm, DateTimeOffset.UtcNow, SourceEdgeId: null)),
                    ct).ConfigureAwait(false);
            }

            await _events.Writer.WriteAsync(
                new StrategyEvent.AgentUtterance("dtmf", ssml, DateTimeOffset.UtcNow),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TTS synthesis failed in DTMF strategy for call {CallId}", _callId);
        }
    }

    private async Task HandleDtmfAsync(DtmfTone tone, CancellationToken ct)
    {
        var current = _executor.Navigator.CurrentStage;
        await _events.Writer.WriteAsync(
            new StrategyEvent.DtmfRecognized(tone.Digit.ToString(), current?.Id, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        if (current?.Blueprint.Channels.Scripted is not { MenuOptions: { Count: > 0 } menu })
        {
            return;
        }

        if (!menu.TryGetValue(tone.Digit, out var option))
        {
            _logger.LogDebug(
                "Stage '{Stage}' has DTMF menu but digit '{Digit}' is not mapped; ignoring.",
                current.Id, tone.Digit);
            return;
        }

        var edge = current.FindEdgeByLabel(option.TransitionLabel);
        if (edge is null)
        {
            _logger.LogWarning(
                "Stage '{Stage}' DTMF maps digit '{Digit}' to label '{Label}', but no outgoing edge matches.",
                current.Id, tone.Digit, option.TransitionLabel);
            return;
        }

        await _executor.AdvanceToAsync(edge.TargetStageId, ct).ConfigureAwait(false);
    }

    internal static bool LooksLikeSsml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return false; }
        var span = text.AsSpan().TrimStart();
        return span.StartsWith("<speak", StringComparison.OrdinalIgnoreCase);
    }
}
