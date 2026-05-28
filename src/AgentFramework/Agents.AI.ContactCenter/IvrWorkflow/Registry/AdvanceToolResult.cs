using System.Text.Json.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Registry;

/// <summary>
/// Structured payload returned by the synthesized IVR <c>advance</c> tool. Serialized to JSON
/// by the function-invocation pipeline so the realtime model can observe the outcome of its
/// transition request (success, validation failure, navigator rejection, …) and react in
/// voice — instead of receiving a canned acknowledgement string regardless of the actual
/// workflow state.
/// </summary>
/// <param name="Status">
/// One of: <c>advanced</c>, <c>advanced_terminal</c>, <c>unknown_choice</c>,
/// <c>intent_without_transition</c>, <c>transition_rejected</c>, <c>no_current_step</c>.
/// </param>
/// <param name="Message">Human-readable summary suitable for the model to verbalize.</param>
/// <param name="From">The step the workflow advanced from (set on success).</param>
/// <param name="To">The step the workflow advanced to (set on success).</param>
/// <param name="Terminal"><see langword="true"/> when the new step is terminal.</param>
/// <param name="Reason">Failure detail (set on rejection / unknown / intent-without-transition).</param>
/// <param name="AllowedTargets">
/// Valid <c>next_stage</c> values for the current step. Populated on
/// <c>unknown_choice</c> so the model can retry with a legal choice.
/// </param>
public sealed record AdvanceToolResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("from")] string? From = null,
    [property: JsonPropertyName("to")] string? To = null,
    [property: JsonPropertyName("terminal")] bool Terminal = false,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("allowed_targets")] IReadOnlyList<string>? AllowedTargets = null)
{
    public const string StatusAdvanced = "advanced";
    public const string StatusAdvancedTerminal = "advanced_terminal";
    public const string StatusUnknownChoice = "unknown_choice";
    public const string StatusIntentWithoutTransition = "intent_without_transition";
    public const string StatusTransitionRejected = "transition_rejected";
    public const string StatusNoCurrentStep = "no_current_step";
}
