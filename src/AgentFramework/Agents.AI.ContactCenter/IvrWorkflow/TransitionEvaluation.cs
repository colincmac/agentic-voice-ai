namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// Result of <see cref="IIvrWorkflowNavigator.EvaluateTransitionAsync"/>. Tells the
/// caller whether the requested transition can fire immediately, must detour through
/// a sub-workflow that satisfies a failing guard, or must be rejected outright when no
/// resolver chain can satisfy the guard.
/// </summary>
/// <remarks>
/// Phase 3 — pairs with the workflow-level <c>authResolvers:</c> table on
/// <see cref="RealtimeIvrWorkflowDefinition.AuthResolvers"/>. The strategy is
/// responsible for acting on the result: pushing the subflow (Phase 1 machinery) for
/// <see cref="RequiresDetour"/>, applying the transition for <see cref="Allowed"/>,
/// or surfacing a rejection for <see cref="BlockedNoResolver"/>.
/// </remarks>
public abstract record TransitionEvaluation
{
    private TransitionEvaluation() { }

    /// <summary>Combined transition + target guards all passed; the strategy may apply the transition directly.</summary>
    public sealed record Allowed(RealtimeIvrWorkflowStep Target) : TransitionEvaluation;

    /// <summary>
    /// A guard failed but a matching <see cref="Compilation.CompiledAuthResolver"/>
    /// supplies a sub-workflow that can satisfy it. The strategy should push
    /// <see cref="ResolverWorkflowId"/> via
    /// <see cref="IIvrWorkflowNavigator.PushSubflowAsync"/> with the original transition
    /// target as the return step; the navigator re-evaluates on pop.
    /// </summary>
    public sealed record RequiresDetour(
        RealtimeIvrWorkflowStep Target,
        string ResolverWorkflowId,
        int? MinVersion,
        int? MaxVersion,
        IIvrStepGuard UnmetGuard,
        string ResolverDescription) : TransitionEvaluation;

    /// <summary>A guard failed and no resolver chain can satisfy it; the strategy should reject the transition.</summary>
    public sealed record BlockedNoResolver(string Reason, IIvrStepGuard UnmetGuard) : TransitionEvaluation;

    /// <summary>The target step id is not a declared transition from the current step.</summary>
    public sealed record Invalid(string Reason) : TransitionEvaluation;
}
