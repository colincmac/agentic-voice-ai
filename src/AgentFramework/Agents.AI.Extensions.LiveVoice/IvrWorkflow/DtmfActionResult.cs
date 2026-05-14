namespace Agents.AI.Extensions.LiveVoice.IvrWorkflow;

/// <summary>
/// Outcome of a DTMF menu option or digit-collection validator. Returned by the
/// <see cref="Microsoft.Extensions.AI.AITool"/> bound to a <see cref="DtmfMenuOption"/>
/// or to <see cref="StepDtmfConfiguration.DigitCollectionValidator"/>. The DTMF
/// strategy pattern-matches on the concrete record to decide what to do next.
/// </summary>
/// <remarks>
/// Returning this type from a tool is the most expressive option, but it is not
/// required. Tools that return a simple envelope with a <c>bool Success</c> property
/// (for example <c>CallControlResult</c>) are interpreted automatically:
/// success transitions to the configured next step, failure speaks the configured
/// failure prompt and stays on the current step.
/// </remarks>
public abstract record DtmfActionResult
{
    /// <summary>Transition to a specific workflow step.</summary>
    public sealed record Transition(string NextStepId) : DtmfActionResult;

    /// <summary>Stay on the current step. Optionally play a prompt instead of the step's default.</summary>
    public sealed record Repeat(string? Prompt = null, Uri? AudioFile = null) : DtmfActionResult;

    /// <summary>
    /// Selection/validation failed. Stay on the current step and speak the supplied
    /// error message (or audio). Use this from a digit-collection validator to
    /// signal "that value isn't acceptable, please try again".
    /// </summary>
    public sealed record Reject(string? ErrorPrompt = null, Uri? ErrorAudioFile = null) : DtmfActionResult;

    /// <summary>
    /// Escalation was requested (e.g. caller pressed 0 to reach a live agent). The
    /// side-effect (transfer/queue/etc.) is expected to have been performed by the
    /// invoked tool; this result just records the intent on the workflow.
    /// </summary>
    public sealed record Escalate(string Reason) : DtmfActionResult;

    /// <summary>
    /// Transfer the call to <paramref name="TargetIdentifier"/>. The DTMF strategy
    /// surfaces this as an <c>OutboundDirective.TransferCall</c> on the active edge
    /// (which must support the <c>TransferCall</c> capability) and then completes the
    /// workflow. Use this for self-service "press 0 for an agent" or "press 9 to be
    /// connected to fraud" routes.
    /// </summary>
    public sealed record Transfer(
        string TargetIdentifier,
        TransferKindHint Kind = TransferKindHint.PhoneNumber,
        string? Reason = null) : DtmfActionResult;

    /// <summary>
    /// The call is being hung up. The side-effect is expected to have been performed
    /// by the invoked tool (e.g. <c>CallControlTools.HangUpCallAsync</c>); this result
    /// tells the strategy not to drive further audio or transitions.
    /// </summary>
    public sealed record HangUp(string? Reason = null) : DtmfActionResult;

    /// <summary>Mark the workflow complete and end driving the conversation.</summary>
    public sealed record Complete(string? Message = null) : DtmfActionResult;
}

/// <summary>
/// Tier-3 hint of how the dispatching edge should interpret the transfer target. Mirrors
/// (but doesn't depend on) the <c>TransferKind</c> enum used by the calling layer so that
/// this extension stays free of a circular reference.
/// </summary>
public enum TransferKindHint
{
    PhoneNumber,
    TeamsUser,
    Consultative
}
