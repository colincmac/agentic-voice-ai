using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// Runtime marker step produced by the compiler when a YAML stage declares
/// <c>type: subflow</c>. Carries the id of the child workflow to push and the parent
/// step ids to return to on success / failure. The realtime strategy intercepts these
/// before rendering: instead of pushing a prompt to the backend it calls
/// <c>IIvrWorkflowNavigator.PushSubflowAsync(...)</c>, which loads the child workflow
/// from <c>IIvrWorkflowCatalog</c> and enters its initial step.
/// </summary>
/// <remarks>
/// Phase 1 subflow stages are realtime-tier-only. DTMF/NLU tiers don't push frames
/// today — encountering a subflow stage in those tiers degrades to "no prompt, no
/// menu" until Phase 3 wires per-tier subflow handling. The compiler emits a warning
/// when a subflow stage is reached via the scripted path.
/// </remarks>
public sealed class SubflowIvrWorkflowStep : RealtimeIvrWorkflowStep
{
    /// <summary>Id of the child workflow to enter when this stage runs. Resolved via <c>IIvrWorkflowCatalog</c>.</summary>
    public required string SubflowWorkflowId { get; init; }

    /// <summary>
    /// Step id in the parent workflow to enter once the child workflow exits via a
    /// non-failure terminal stage. <see langword="null"/> means "the parent workflow ends
    /// when this subflow completes" (rare; usually a real return target is set).
    /// </summary>
    public string? OnSuccessStepId { get; init; }

    /// <summary>
    /// Step id in the parent workflow to enter when the child workflow exits via a
    /// failure terminal stage. <see langword="null"/> means failure terminates the parent.
    /// </summary>
    public string? OnFailureStepId { get; init; }

    /// <summary>Phase 2: lower-bound integer version constraint passed to the catalog at push time. <see langword="null"/> means unbounded.</summary>
    public int? MinVersion { get; init; }

    /// <summary>Phase 2: upper-bound integer version constraint passed to the catalog at push time.</summary>
    public int? MaxVersion { get; init; }
}
