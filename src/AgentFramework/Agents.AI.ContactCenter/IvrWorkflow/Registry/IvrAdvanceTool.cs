using System.ComponentModel;
using System.Linq;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.IvrWorkflow.Registry;

/// <summary>
/// Builds the synthetic <c>advance</c> tool that a realtime / chat-completion model can
/// invoke to drive an IVR workflow transition. The tool is generated per-stage because
/// the set of valid <c>next_stage</c> values is stage-specific — it is the union of:
/// <list type="bullet">
///   <item>the step's outgoing <see cref="RealtimeIvrWorkflowStep.ValidTransitions"/>,</item>
///   <item>and each declared intent name on the step (so the orchestrator can map
///     intent-driven authoring back to an explicit transition decision).</item>
/// </list>
/// The strategy listens for <c>RealtimeBackendUpdate.FunctionCalled(Name = "advance")</c>
/// and translates the chosen value into a navigator transition: a step id matches
/// directly, while an intent name resolves through <see cref="RealtimeIvrWorkflowStep.Intents"/>
/// to the intent's declared <c>NextStepId</c>.
/// </summary>
public static class IvrAdvanceTool
{
    /// <summary>The canonical name the realtime model invokes to advance a stage.</summary>
    public const string AdvanceToolName = "advance";

    /// <summary>The name of the single string argument the model fills with the chosen target.</summary>
    public const string NextStageArgumentName = "next_stage";

    /// <summary>
    /// Build the advance tool for <paramref name="step"/>, or return <see langword="null"/>
    /// when the step has no transitions or intents (terminal / pure-DTMF stages do not
    /// need an advance tool because the runtime drives termination directly).
    /// </summary>
    /// <remarks>
    /// The returned <see cref="AIFunction"/> delegates to
    /// <paramref name="invoker"/>.<see cref="IvrAdvanceToolInvoker.InvokeAsync(string, CancellationToken)"/>,
    /// which runs the resolve → guard-aware transition → backend re-arm sequence inline
    /// and returns a structured <see cref="AdvanceToolResult"/>. Because the function
    /// body executes under the realtime client's <c>UseFunctionInvocation()</c> pipeline,
    /// the model receives the structured result as a tool response and can self-correct
    /// on validation failures (unknown choice, rejected transition, …) instead of
    /// speaking as if the workflow advanced.
    /// </remarks>
    public static AIFunction? TryCreate(RealtimeIvrWorkflowStep step, IvrAdvanceToolInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(invoker);

        var targets = CollectAdvanceTargets(step);
        if (targets.Count == 0)
        {
            return null;
        }

        var allowed = string.Join(", ", targets);
        var description =
            $"Advance the IVR workflow to the next stage. Set '{NextStageArgumentName}' to one of: {allowed}. " +
            "Call this immediately once the caller's intent for the current stage is clear; do not call it before that. " +
            "The tool returns a structured result with a 'status' field ('advanced', 'advanced_terminal', " +
            "'unknown_choice', 'intent_without_transition', 'transition_rejected', 'no_current_step') and a " +
            "human-readable 'message'. If status is not 'advanced' or 'advanced_terminal', do not assume the " +
            "workflow moved on — read the message and react accordingly.";

        return AIFunctionFactory.Create(
            ([Description("The next stage or intent name to transition to. Must be one of the allowed values listed in the tool description.")] string next_stage,
             CancellationToken cancellationToken) => invoker.InvokeAsync(next_stage, cancellationToken),
            name: AdvanceToolName,
            description: description);
    }

    /// <summary>
    /// Returns the ordered, distinct set of advance targets for a step (intent names first,
    /// then any transition next-step ids not already implied by an intent).
    /// </summary>
    public static IReadOnlyList<string> CollectAdvanceTargets(RealtimeIvrWorkflowStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();

        foreach (var intent in step.Intents.Values)
        {
            if (!string.IsNullOrWhiteSpace(intent.Name) && seen.Add(intent.Name))
            {
                ordered.Add(intent.Name);
            }
        }

        foreach (var nextStep in step.ValidTransitions)
        {
            if (!string.IsNullOrWhiteSpace(nextStep) && seen.Add(nextStep))
            {
                ordered.Add(nextStep);
            }
        }

        return ordered;
    }

    /// <summary>
    /// Resolve the model's chosen value against the step's intents and transitions, returning
    /// the actual stage id to transition to. Intent names are preferred over raw stage ids so
    /// authoring with intents keeps the orchestration metadata (capability / confirm prompt)
    /// reachable to the caller.
    /// </summary>
    public static AdvanceResolution Resolve(RealtimeIvrWorkflowStep step, string chosen)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (string.IsNullOrWhiteSpace(chosen))
        {
            return AdvanceResolution.Unknown(chosen);
        }

        var trimmed = chosen.Trim();

        if (step.Intents.TryGetValue(trimmed, out var intent))
        {
            if (intent.NextStepId is { Length: > 0 } intentTarget)
            {
                return AdvanceResolution.Intent(intent, intentTarget);
            }
            // Intent with no next-stage — still surface it so the caller can react
            // (for example by invoking a capability).
            return AdvanceResolution.IntentWithoutTransition(intent);
        }

        foreach (var transition in step.ValidTransitions)
        {
            if (string.Equals(transition, trimmed, StringComparison.Ordinal))
            {
                return AdvanceResolution.Stage(trimmed);
            }
        }

        return AdvanceResolution.Unknown(trimmed);
    }
}

/// <summary>
/// Outcome of resolving the model's chosen <c>next_stage</c> against the step's intent and
/// transition table.
/// </summary>
public sealed class AdvanceResolution
{
    private AdvanceResolution() { }

    public string? Chosen { get; private init; }
    public string? TargetStageId { get; private init; }
    public RealtimeIvrWorkflowIntent? ResolvedIntent { get; private init; }
    public AdvanceResolutionKind Kind { get; private init; }

    public bool IsTransition => Kind is AdvanceResolutionKind.Intent or AdvanceResolutionKind.Stage;

    public static AdvanceResolution Intent(RealtimeIvrWorkflowIntent intent, string targetStageId) =>
        new() { Kind = AdvanceResolutionKind.Intent, Chosen = intent.Name, ResolvedIntent = intent, TargetStageId = targetStageId };

    public static AdvanceResolution IntentWithoutTransition(RealtimeIvrWorkflowIntent intent) =>
        new() { Kind = AdvanceResolutionKind.IntentWithoutTransition, Chosen = intent.Name, ResolvedIntent = intent };

    public static AdvanceResolution Stage(string stageId) =>
        new() { Kind = AdvanceResolutionKind.Stage, Chosen = stageId, TargetStageId = stageId };

    public static AdvanceResolution Unknown(string? chosen) =>
        new() { Kind = AdvanceResolutionKind.Unknown, Chosen = chosen };
}

public enum AdvanceResolutionKind
{
    Unknown,
    Stage,
    Intent,
    IntentWithoutTransition,
}
