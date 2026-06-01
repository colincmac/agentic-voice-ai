using System.Text.Json.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Registry;

/// <summary>
/// Structured payload returned by a synthesized <c>advance_to_{stageId}</c> tool. Serialized
/// to JSON by the function-invocation pipeline so the realtime model can observe the
/// outcome of its transition request (success, navigator rejection, …) and react in voice
/// — instead of receiving a canned acknowledgement string regardless of the actual workflow
/// state.
/// </summary>
/// <param name="Status">
/// One of: <c>advanced</c>, <c>advanced_terminal</c>, <c>transition_rejected</c>,
/// <c>no_current_step</c>.
/// </param>
/// <param name="Message">Human-readable summary suitable for the model to verbalize.</param>
/// <param name="From">The step the workflow advanced from (set whenever a current step existed).</param>
/// <param name="To">The step the workflow advanced to (set on success; also echoed on rejection).</param>
/// <param name="Terminal"><see langword="true"/> when the new step is terminal.</param>
/// <param name="Reason">
/// Failure detail (set on rejection) or the optional reason string the model supplied
/// when invoking the function (echoed on success for trace correlation).
/// </param>
public sealed record AdvanceToolResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("from")] string? From = null,
    [property: JsonPropertyName("to")] string? To = null,
    [property: JsonPropertyName("terminal")] bool Terminal = false,
    [property: JsonPropertyName("reason")] string? Reason = null)
{
    public const string StatusAdvanced = "advanced";
    public const string StatusAdvancedTerminal = "advanced_terminal";
    public const string StatusTransitionRejected = "transition_rejected";

    /// <summary>
    /// Defensive status emitted when an <c>advance_to_*</c> function fires after the
    /// navigator's current step has been cleared (e.g. between teardown and the model
    /// observing the final tool-surface update). Not reachable through normal flow.
    /// </summary>
    public const string StatusNoCurrentStep = "no_current_step";
}
