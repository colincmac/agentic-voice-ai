using System.Text;
using System.Threading.Channels;
using Agents.AI.ContactCenter.IvrWorkflow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Calling.Strategies.Dtmf;

/// <summary>
/// Shared DTMF input handler used by strategies that mix DTMF with another conversation
/// modality (RealtimeVoice, NLU). Encapsulates:
/// <list type="bullet">
///   <item><c>DtmfRecognized</c> event emission for observability.</item>
///   <item>Buffered <c>scripted.dtmf.collect</c> digit collection (terminator / max-digits / min-digits).</item>
///   <item><c>scripted.dtmf.options</c> menu lookup and tool invocation through the navigator.</item>
///   <item>Translation of <see cref="DtmfActionResult"/> into navigator transitions
///         (always routed through <see cref="IIvrWorkflowNavigator.EvaluateTransitionAsync"/>
///         so guard detours fire correctly on every tier).</item>
/// </list>
/// All strategy-specific I/O (LLM text turns, TTS playback, transfer directives, session
/// teardown) is delegated to <see cref="IDtmfStrategySink"/>. Per-strategy nuances
/// (forward unrecognized digits to the LLM vs. silently ignore, repeat via LLM hint vs.
/// resynthesize) live in the sink.
/// </summary>
public sealed class DtmfInputProcessor
{
    private readonly IvrWorkflowSession _session;
    private readonly IDtmfStrategySink _sink;
    private readonly ChannelWriter<StrategyEvent> _events;
    private readonly ILogger _logger;
    private readonly StringBuilder _buffer = new();

    public DtmfInputProcessor(
        IvrWorkflowSession session,
        IDtmfStrategySink sink,
        ChannelWriter<StrategyEvent> events,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(events);

        _session = session;
        _sink = sink;
        _events = events;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Handle a single inbound DTMF tone. Emits <see cref="StrategyEvent.DtmfRecognized"/>
    /// for observability, then dispatches based on the current step's scripted DTMF
    /// configuration. No-ops when there is no current step.
    /// </summary>
    public async Task ProcessAsync(DtmfTone tone, CancellationToken cancellationToken)
    {
        var step = _session.Navigator.CurrentStep;
        if (step is null)
        {
            return;
        }

        await _events.WriteAsync(
            new StrategyEvent.DtmfRecognized(tone.Digit.ToString(), step.Id, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        var dtmf = step.StepScriptedConfiguration?.Dtmf;
        var hasMenu = dtmf?.MenuOptions is { Count: > 0 };
        var hasCollect = dtmf?.DigitCollectionValidator is not null
            || !string.IsNullOrEmpty(dtmf?.OnValidNextStepId);

        if (hasMenu)
        {
            await HandleMenuDigitAsync(step, tone.Digit, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (hasCollect)
        {
            await HandleCollectedDigitAsync(step, dtmf!, tone.Digit, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _sink.OnUnconfiguredDigitAsync(tone, step, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleMenuDigitAsync(RealtimeIvrWorkflowStep step, char digit, CancellationToken ct)
    {
        if (!_session.Navigator.TryResolveDtmfDigit(digit, out var option))
        {
            await _sink.OnUnmatchedMenuDigitAsync(new DtmfTone(digit, DateTimeOffset.UtcNow), step, ct).ConfigureAwait(false);
            return;
        }

        _session.State.Set($"{step.Id}_selection", option.Label);

        var actionResult = await _session.Navigator
            .InvokeMenuActionAsync(option, extraArguments: null, ct)
            .ConfigureAwait(false);

        await DispatchAsync(actionResult, step, ct).ConfigureAwait(false);
    }

    private async Task HandleCollectedDigitAsync(
        RealtimeIvrWorkflowStep step,
        StepDtmfConfiguration dtmf,
        char digit,
        CancellationToken ct)
    {
        var terminator = dtmf.TerminationDigitChar;
        var maxDigits = dtmf.MaxNumberOfDigits <= 0 ? int.MaxValue : dtmf.MaxNumberOfDigits;

        string? collected;
        lock (_buffer)
        {
            if (digit == terminator)
            {
                collected = _buffer.ToString();
                _buffer.Clear();
            }
            else
            {
                _buffer.Append(digit);
                if (_buffer.Length >= maxDigits)
                {
                    collected = _buffer.ToString();
                    _buffer.Clear();
                }
                else
                {
                    collected = null;
                }
            }
        }

        if (string.IsNullOrEmpty(collected))
        {
            return;
        }

        if (dtmf.MinNumberOfDigits > 0 && collected.Length < dtmf.MinNumberOfDigits)
        {
            await _sink.OnIncompleteBufferAsync(collected, dtmf.MinNumberOfDigits, step, ct).ConfigureAwait(false);
            return;
        }

        if (dtmf.DigitCollectionValidator is { } validator)
        {
            var stateKey = dtmf.CollectedStateKey ?? $"{step.Id}_collected";
            var extra = new Dictionary<string, object?>
            {
                [dtmf.DigitsParameterName] = collected,
            };

            var actionResult = await _session.Navigator.InvokeActionAsync(
                validator,
                dtmf.DigitCollectionArguments,
                extraArguments: extra,
                successNextStepId: dtmf.OnValidNextStepId,
                failurePrompt: dtmf.OnInvalidPrompt,
                failureAudio: dtmf.OnInvalidAudioFile,
                ct).ConfigureAwait(false);

            if (actionResult is DtmfActionResult.Transition or DtmfActionResult.Complete)
            {
                _session.State.Set(stateKey, collected);
            }

            await DispatchAsync(actionResult, step, ct).ConfigureAwait(false);
            return;
        }

        // No validator: store under the default key and walk the first declared transition.
        _session.State.Set(dtmf.CollectedStateKey ?? $"{step.Id}_collected", collected);
        if (dtmf.OnValidNextStepId is { Length: > 0 } onValid)
        {
            await DispatchAsync(new DtmfActionResult.Transition(onValid), step, ct).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(
        DtmfActionResult result,
        RealtimeIvrWorkflowStep step,
        CancellationToken ct)
    {
        switch (result)
        {
            case DtmfActionResult.Transition transition:
                await ApplyTransitionAsync(transition.NextStepId, step, ct).ConfigureAwait(false);
                break;

            case DtmfActionResult.Reject reject:
                await _sink.RejectAsync(reject, step, ct).ConfigureAwait(false);
                break;

            case DtmfActionResult.Repeat repeat:
                await _sink.RepeatAsync(repeat, step, ct).ConfigureAwait(false);
                break;

            case DtmfActionResult.Complete:
                await _sink.EndSessionAsync("DTMF action completed the workflow", ct).ConfigureAwait(false);
                break;

            case DtmfActionResult.HangUp:
                await _sink.EndSessionAsync("DTMF triggered hang-up", ct).ConfigureAwait(false);
                break;

            case DtmfActionResult.Transfer transfer:
                await _sink.TransferAsync(transfer, ct).ConfigureAwait(false);
                break;

            case DtmfActionResult.Escalate escalate:
                await _sink.EscalateAsync(escalate.Reason, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Route a transition through <see cref="IIvrWorkflowNavigator.EvaluateTransitionAsync"/>
    /// so per-transition guards / auth resolvers fire before the navigator mutates.
    /// Successful evaluations land at <see cref="IDtmfStrategySink.ApplyStepAsync"/>;
    /// detours push the resolver subflow and land at its initial step; blocked / invalid
    /// transitions surface to <see cref="IDtmfStrategySink.OnTransitionBlockedAsync"/>.
    /// </summary>
    private async Task ApplyTransitionAsync(
        string nextStepId,
        RealtimeIvrWorkflowStep currentStep,
        CancellationToken ct)
    {
        var eval = await _session.Navigator.EvaluateTransitionAsync(nextStepId, ct).ConfigureAwait(false);
        switch (eval)
        {
            case TransitionEvaluation.Allowed allowed:
            {
                var tr = _session.Navigator.TransitionTo(allowed.Target.Id);
                if (tr.Succeeded && tr.NewStep is not null)
                {
                    await _sink.ApplyStepAsync(tr.NewStep, ct).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogWarning(
                        "DTMF requested transition to '{Target}' but it was rejected after Allowed evaluation: {Reason}",
                        allowed.Target.Id, tr.Reason);
                }
                break;
            }

            case TransitionEvaluation.RequiresDetour detour:
            {
                _session.State.Set(
                    PendingIntent.StateKey,
                    new PendingIntent(detour.Target.Id, _session.Navigator.Definition.Name));
                _logger.LogInformation(
                    "DTMF transition to '{Target}' detouring through '{Subflow}' to satisfy '{Guard}'.",
                    detour.Target.Id, detour.ResolverWorkflowId, detour.UnmetGuard.GetType().Name);
                var childInitial = await _session.Navigator.PushSubflowAsync(
                    detour.ResolverWorkflowId,
                    returnToStepId: detour.Target.Id,
                    failureReturnStepId: detour.Target.OnUnauthorizedStepId
                        ?? _session.Navigator.Definition.UnauthorizedFailureStepId,
                    detour.MinVersion,
                    detour.MaxVersion,
                    ct).ConfigureAwait(false);
                await _sink.ApplyStepAsync(childInitial, ct).ConfigureAwait(false);
                break;
            }

            case TransitionEvaluation.BlockedNoResolver blocked:
                _logger.LogWarning(
                    "DTMF transition to '{Target}' blocked: {Reason} (no resolver for guard '{Guard}')",
                    nextStepId, blocked.Reason, blocked.UnmetGuard.GetType().Name);
                await _sink.OnTransitionBlockedAsync(nextStepId, blocked.Reason, currentStep, ct).ConfigureAwait(false);
                break;

            case TransitionEvaluation.Invalid invalid:
                _logger.LogWarning(
                    "DTMF requested transition to '{Target}' but it was rejected: {Reason}",
                    nextStepId, invalid.Reason);
                break;
        }
    }
}

/// <summary>
/// Strategy-specific side-effect callbacks <see cref="DtmfInputProcessor"/> uses to
/// surface DTMF events to the caller. Implementations decide whether to speak through
/// a TTS synthesizer (NLU), surface as inline LLM text (RealtimeVoice), or emit verb
/// directives (DTMF-only strategies).
/// </summary>
public interface IDtmfStrategySink
{
    /// <summary>
    /// Apply the freshly-resolved <paramref name="step"/>: render its prompt + tool
    /// surface to the caller-facing pipeline. Called after every successful transition
    /// (including detour pushes).
    /// </summary>
    Task ApplyStepAsync(RealtimeIvrWorkflowStep step, CancellationToken cancellationToken);

    /// <summary>Surface a <see cref="DtmfActionResult.Reject"/> to the caller.</summary>
    Task RejectAsync(DtmfActionResult.Reject reject, RealtimeIvrWorkflowStep step, CancellationToken cancellationToken);

    /// <summary>Surface a <see cref="DtmfActionResult.Repeat"/> (re-prompt the caller).</summary>
    Task RepeatAsync(DtmfActionResult.Repeat repeat, RealtimeIvrWorkflowStep step, CancellationToken cancellationToken);

    /// <summary>Wind the session down — workflow reached a terminal action.</summary>
    Task EndSessionAsync(string reason, CancellationToken cancellationToken);

    /// <summary>Issue a transfer directive and tear the session down.</summary>
    Task TransferAsync(DtmfActionResult.Transfer transfer, CancellationToken cancellationToken);

    /// <summary>Emit an escalation event for orchestration to act on.</summary>
    Task EscalateAsync(string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Handle a digit pressed on a menu stage that doesn't match any registered option.
    /// RealtimeVoice surfaces the digit to the LLM; NLU silently re-prompts.
    /// </summary>
    Task OnUnmatchedMenuDigitAsync(DtmfTone tone, RealtimeIvrWorkflowStep step, CancellationToken cancellationToken);

    /// <summary>
    /// Handle a digit pressed on a stage with no <c>scripted.dtmf</c> configuration at
    /// all. RealtimeVoice forwards as a synthetic user-text turn; NLU ignores.
    /// </summary>
    Task OnUnconfiguredDigitAsync(DtmfTone tone, RealtimeIvrWorkflowStep step, CancellationToken cancellationToken);

    /// <summary>
    /// Handle a buffered collect that committed below <c>minNumberOfDigits</c>. The
    /// processor has already drained the buffer; the sink decides how to surface the
    /// shortfall (re-prompt, reject prompt, ignore).
    /// </summary>
    Task OnIncompleteBufferAsync(string collected, int minRequired, RealtimeIvrWorkflowStep step, CancellationToken cancellationToken);

    /// <summary>
    /// Handle a transition that <see cref="IIvrWorkflowNavigator.EvaluateTransitionAsync"/>
    /// blocked because no auth resolver could satisfy a guard. RealtimeVoice surfaces
    /// the reason as inline LLM text; NLU re-prompts.
    /// </summary>
    Task OnTransitionBlockedAsync(string targetStepId, string reason, RealtimeIvrWorkflowStep step, CancellationToken cancellationToken);
}
