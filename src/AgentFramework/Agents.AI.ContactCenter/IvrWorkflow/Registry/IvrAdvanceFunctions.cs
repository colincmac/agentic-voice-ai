using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.IvrWorkflow.Registry;

/// <summary>
/// Builds the per-call set of synthesized <c>advance_to_{stageId}</c> AI tools that a
/// realtime / chat-completion model can invoke to drive an IVR workflow transition, and
/// runs the resolve → guard-aware transition → backend re-arm pipeline inline when the
/// model picks one.
/// </summary>
/// <remarks>
/// <para>
/// Modeled on the <see href="https://github.com/microsoft/agent-framework/blob/main/dotnet/src/Microsoft.Agents.AI.Workflows/Specialized/HandoffAgentExecutor.cs">
/// Microsoft Agent Framework Handoff orchestrator</see>: instead of exposing a single
/// <c>advance(next_stage: string)</c> tool whose argument was constrained only by a
/// natural-language description, each valid transition surfaces as its own function on
/// the tool surface. The model literally cannot name an unknown target — it can only
/// call functions we registered.
/// </para>
/// <para>
/// Compared to Handoff, our function bodies are executable rather than declaration-only:
/// transitions can require an auth-resolver detour, be rejected by a navigator guard, or
/// land on a terminal stage. Each outcome is surfaced as a typed
/// <see cref="AdvanceToolResult"/> the model can verbalize, instead of Handoff's canned
/// <c>"Transferred."</c> string.
/// </para>
/// <para>
/// Built and rebuilt on every stage entry by the strategies' render pipeline — the set
/// of valid transitions changes with the current step. Intents that have no
/// <see cref="RealtimeIvrWorkflowIntent.NextStepId"/> are <em>not</em> exposed as
/// advance functions; they belong on the capability tool surface (<see cref="RealtimeIvrWorkflowStep.AvailableTools"/>)
/// instead.
/// </para>
/// <para>
/// Requires the realtime conversation client pipeline to include
/// <c>UseFunctionInvocation()</c>; otherwise the tool bodies never run.
/// </para>
/// </remarks>
public sealed class IvrAdvanceFunctions
{
    /// <summary>
    /// Prefix every advance function name starts with. Used by strategies to recognize
    /// advance calls in their <c>FunctionCalled</c> bus handlers when telemetry needs
    /// to discriminate IVR navigation from tenant tools.
    /// </summary>
    public const string FunctionPrefix = "advance_to_";

    private readonly IIvrWorkflowNavigator _navigator;
    private readonly Func<RealtimeIvrWorkflowStep, CancellationToken, Task> _applyStageAsync;
    private readonly ILogger _logger;

    /// <param name="navigator">The per-call navigator that owns the workflow state machine.</param>
    /// <param name="applyStageAsync">
    /// Strategy callback invoked after a successful navigator transition (or after a
    /// detour is pushed) to push the new step's prompt + tool surface onto the realtime
    /// backend. Must serialize with any other navigator mutators.
    /// </param>
    /// <param name="logger">Optional logger; defaults to <see cref="NullLogger"/>.</param>
    public IvrAdvanceFunctions(
        IIvrWorkflowNavigator navigator,
        Func<RealtimeIvrWorkflowStep, CancellationToken, Task> applyStageAsync,
        ILogger<IvrAdvanceFunctions>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        ArgumentNullException.ThrowIfNull(applyStageAsync);

        _navigator = navigator;
        _applyStageAsync = applyStageAsync;
        _logger = logger ?? NullLogger<IvrAdvanceFunctions>.Instance;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="functionName"/> is a synthesized
    /// advance function (i.e. begins with <see cref="FunctionPrefix"/>).
    /// </summary>
    public static bool IsAdvanceFunctionName(string? functionName) =>
        !string.IsNullOrEmpty(functionName)
        && functionName.StartsWith(FunctionPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Synthesize one <see cref="AIFunction"/> per valid advance target on <paramref name="step"/>.
    /// Returns an empty enumeration for terminal stages and for stages with neither
    /// transitions nor transition-bearing intents (the strategy should not add anything
    /// to its tool surface in those cases).
    /// </summary>
    /// <remarks>
    /// Targets are ordered as: each intent's declared <c>NextStepId</c> (preserving intent
    /// metadata for the function description), then any raw transition target not already
    /// produced by an intent. Duplicates (intent target == transition target) are deduped
    /// in favor of the intent variant so the model sees the richer description.
    /// </remarks>
    public IEnumerable<AIFunction> BuildForStep(RealtimeIvrWorkflowStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (step.Terminal)
        {
            yield break;
        }

        var emittedTargets = new HashSet<string>(StringComparer.Ordinal);
        var emittedFunctionNames = new HashSet<string>(StringComparer.Ordinal);

        // Intents first so their metadata (name, confirm prompt) shapes the function
        // description when an intent and a raw transition resolve to the same target.
        foreach (var intent in step.Intents.Values)
        {
            if (string.IsNullOrWhiteSpace(intent.NextStepId)
                || !emittedTargets.Add(intent.NextStepId))
            {
                continue;
            }

            var fn = BuildFunction(
                step,
                targetStageId: intent.NextStepId,
                intentName: intent.Name,
                confirmPrompt: intent.ConfirmPrompt,
                emittedFunctionNames);
            if (fn is not null)
            {
                yield return fn;
            }
        }

        foreach (var transitionTarget in step.ValidTransitions)
        {
            if (string.IsNullOrWhiteSpace(transitionTarget)
                || !emittedTargets.Add(transitionTarget))
            {
                continue;
            }

            var fn = BuildFunction(
                step,
                targetStageId: transitionTarget,
                intentName: null,
                confirmPrompt: null,
                emittedFunctionNames);
            if (fn is not null)
            {
                yield return fn;
            }
        }
    }

    private AIFunction? BuildFunction(
        RealtimeIvrWorkflowStep fromStep,
        string targetStageId,
        string? intentName,
        string? confirmPrompt,
        HashSet<string> emittedFunctionNames)
    {
        var functionName = FunctionNameFor(targetStageId);
        if (!emittedFunctionNames.Add(functionName))
        {
            // Two different stage ids that sanitize to the same function name. Skip the
            // collision; the first one wins. Highly unlikely with kebab/snake-case ids.
            _logger.LogWarning(
                "Skipping advance target '{Target}' on step '{Step}' — function name '{FunctionName}' already in use.",
                targetStageId, fromStep.Id, functionName);
            return null;
        }

        var description = BuildDescription(targetStageId, intentName, confirmPrompt);

        // Capture targetStageId + intentName in the closure so the runtime never has to
        // parse them back out of the function name. `reason` has a default so the
        // function-invocation pipeline treats it as optional.
        return AIFunctionFactory.Create(
            ([Description("Brief reason this transition is appropriate. Optional; used for tracing.")] string? reason = null,
             CancellationToken cancellationToken = default) =>
                AdvanceToAsync(targetStageId, intentName, reason, cancellationToken),
            name: functionName,
            description: description);
    }

    private static string BuildDescription(string targetStageId, string? intentName, string? confirmPrompt)
    {
        var basePart = intentName is { Length: > 0 } intent
            ? $"Advance the workflow to stage '{targetStageId}' when the caller's intent matches '{intent}'."
            : $"Advance the workflow to stage '{targetStageId}'.";

        var confirmPart = !string.IsNullOrWhiteSpace(confirmPrompt)
            ? $" The system may confirm the transition by speaking: \"{confirmPrompt}\"."
            : string.Empty;

        return basePart + confirmPart +
            " The tool returns a structured result with a 'status' field ('advanced', " +
            "'advanced_terminal', 'transition_rejected', 'no_current_step') and a " +
            "human-readable 'message'. If status is not 'advanced' or 'advanced_terminal', " +
            "do not assume the workflow moved on — read the message and react accordingly.";
    }

    /// <summary>
    /// Sanitize <paramref name="targetStageId"/> into an OpenAI-compliant function name
    /// (<c>[a-zA-Z0-9_-]+</c>) prefixed with <see cref="FunctionPrefix"/>.
    /// </summary>
    public static string FunctionNameFor(string targetStageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStageId);

        // Fast path: most stage ids in our YAML are already kebab/snake-case.
        var ok = true;
        for (int i = 0; i < targetStageId.Length; i++)
        {
            var c = targetStageId[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-'))
            {
                ok = false;
                break;
            }
        }
        if (ok)
        {
            return FunctionPrefix + targetStageId;
        }

        var buf = new char[targetStageId.Length];
        for (int i = 0; i < targetStageId.Length; i++)
        {
            var c = targetStageId[i];
            buf[i] = char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' ? c : '_';
        }
        return FunctionPrefix + new string(buf);
    }

    private async Task<AdvanceToolResult> AdvanceToAsync(
        string targetStageId,
        string? intentName,
        string? reason,
        CancellationToken cancellationToken)
    {
        var currentStep = _navigator.CurrentStep;
        if (currentStep is null)
        {
            _logger.LogWarning(
                "advance_to function for target '{Target}' invoked but navigator has no current step.",
                targetStageId);
            return new AdvanceToolResult(
                AdvanceToolResult.StatusNoCurrentStep,
                "Cannot advance: the workflow has no current step.",
                To: targetStageId,
                Reason: reason);
        }

        var evaluation = await _navigator.EvaluateTransitionAsync(targetStageId, cancellationToken).ConfigureAwait(false);

        switch (evaluation)
        {
            case TransitionEvaluation.Allowed:
            {
                var tr = _navigator.TransitionTo(targetStageId);
                if (!tr.Succeeded || tr.NewStep is null)
                {
                    _logger.LogWarning(
                        "Advance to '{Target}' from '{Current}' rejected by navigator after Allowed evaluation: {Reason}",
                        targetStageId, currentStep.Id, tr.Reason);
                    return new AdvanceToolResult(
                        AdvanceToolResult.StatusTransitionRejected,
                        $"Cannot advance to '{targetStageId}': {tr.Reason ?? "navigator rejected the transition."}",
                        From: currentStep.Id,
                        To: targetStageId,
                        Reason: tr.Reason ?? reason);
                }

                await _applyStageAsync(tr.NewStep, cancellationToken).ConfigureAwait(false);

                var terminal = tr.NewStep.Terminal;
                return new AdvanceToolResult(
                    terminal ? AdvanceToolResult.StatusAdvancedTerminal : AdvanceToolResult.StatusAdvanced,
                    terminal
                        ? $"Advanced to terminal stage '{tr.NewStep.Id}'. The workflow is complete."
                        : $"Advanced to stage '{tr.NewStep.Id}'.",
                    From: currentStep.Id,
                    To: tr.NewStep.Id,
                    Terminal: terminal,
                    Reason: reason);
            }

            case TransitionEvaluation.RequiresDetour detour:
            {
                // Cache the original intent so subflow prompts can surface it (Collected
                // Information renders all state keys automatically), then push the
                // resolver subflow with the original target as the return step.
                _navigator.State.Set(
                    PendingIntent.StateKey,
                    new PendingIntent(targetStageId, _navigator.Definition.Name, intentName));

                _logger.LogInformation(
                    "Advance to '{Target}' detouring through '{Resolver}' ({Subflow}) to satisfy '{Guard}'.",
                    targetStageId, detour.ResolverDescription, detour.ResolverWorkflowId, detour.UnmetGuard.GetType().Name);

                var childInitial = await _navigator.PushSubflowAsync(
                    detour.ResolverWorkflowId,
                    returnToStepId: targetStageId,
                    failureReturnStepId: detour.Target.OnUnauthorizedStepId
                        ?? _navigator.Definition.UnauthorizedFailureStepId,
                    detour.MinVersion,
                    detour.MaxVersion,
                    cancellationToken).ConfigureAwait(false);

                await _applyStageAsync(childInitial, cancellationToken).ConfigureAwait(false);

                return new AdvanceToolResult(
                    AdvanceToolResult.StatusAdvanced,
                    $"Routing through '{detour.ResolverWorkflowId}' to satisfy {detour.ResolverDescription} before continuing to '{targetStageId}'.",
                    From: currentStep.Id,
                    To: childInitial.Id,
                    Reason: reason);
            }

            case TransitionEvaluation.BlockedNoResolver blocked:
                _logger.LogWarning(
                    "Advance to '{Target}' blocked: {Reason} (no resolver registered for guard '{Guard}')",
                    targetStageId, blocked.Reason, blocked.UnmetGuard.GetType().Name);
                return new AdvanceToolResult(
                    AdvanceToolResult.StatusTransitionRejected,
                    $"Cannot advance to '{targetStageId}': {blocked.Reason}",
                    From: currentStep.Id,
                    To: targetStageId,
                    Reason: blocked.Reason);

            case TransitionEvaluation.Invalid invalid:
                _logger.LogWarning(
                    "Advance to '{Target}' from '{Current}' rejected by navigator: {Reason}",
                    targetStageId, currentStep.Id, invalid.Reason);
                return new AdvanceToolResult(
                    AdvanceToolResult.StatusTransitionRejected,
                    $"Cannot advance to '{targetStageId}': {invalid.Reason}",
                    From: currentStep.Id,
                    To: targetStageId,
                    Reason: invalid.Reason);

            default:
                // Defensive: unreachable while TransitionEvaluation stays sealed.
                return new AdvanceToolResult(
                    AdvanceToolResult.StatusTransitionRejected,
                    $"Unrecognized transition evaluation for '{targetStageId}'.",
                    From: currentStep.Id,
                    To: targetStageId,
                    Reason: reason);
        }
    }
}
