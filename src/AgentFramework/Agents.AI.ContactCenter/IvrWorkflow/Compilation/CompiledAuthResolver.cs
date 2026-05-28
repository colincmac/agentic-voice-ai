using Agents.AI.ContactCenter.Authentication;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>
/// Compiled auth-resolver entry. Tells the navigator which sub-workflow to push when a
/// transition's guard fails. Matching is shape-based via <see cref="Matches"/>, which
/// the compiler builds from the YAML <c>guard:</c> document.
/// </summary>
/// <remarks>
/// Phase 3 supports auth-level resolvers and state-key resolvers out of the box; custom
/// guard kinds can be matched by passing a custom <see cref="Matches"/> predicate.
/// </remarks>
public sealed class CompiledAuthResolver
{
    /// <summary>Predicate that returns true when this resolver satisfies the supplied guard instance.</summary>
    public required Func<IIvrStepGuard, bool> Matches { get; init; }

    /// <summary>Sub-workflow id to push when <see cref="Matches"/> returns true for a failing guard.</summary>
    public required string SubflowWorkflowId { get; init; }

    /// <summary>Optional lower-bound version constraint passed to the catalog at push time.</summary>
    public int? MinVersion { get; init; }

    /// <summary>Optional upper-bound version constraint.</summary>
    public int? MaxVersion { get; init; }

    /// <summary>Human-readable description for diagnostics ("auth:multiFactor", "state:identityConfirmed", etc.).</summary>
    public string Description { get; init; } = string.Empty;
}
