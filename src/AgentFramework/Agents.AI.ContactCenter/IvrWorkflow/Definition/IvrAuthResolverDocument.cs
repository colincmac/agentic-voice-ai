using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Definition;

/// <summary>
/// Workflow-level entry in the <c>authResolvers:</c> block. Maps a guard pattern (typed
/// like the existing <see cref="IvrGuardDocument"/>) to a sub-workflow whose successful
/// completion satisfies it. Used by the Phase 3 navigator detour logic: when a
/// transition's <c>requires:</c> (or the target stage's <c>requires:</c>) fails, the
/// navigator looks up the first matching resolver and pushes the named subflow with
/// the original transition target as the return step. On pop the navigator re-evaluates
/// the guard; if still failing it chains to the next matching resolver, and so on.
/// </summary>
/// <remarks>
/// Matching is shape-based: a resolver applies to a guard when the resolver's
/// <see cref="Guard"/> document, compiled into an <see cref="Guards.IIvrGuardFactory"/>,
/// produces a guard of the same kind and (for built-ins) the same target level / state
/// key. Phase 3 supports <c>type: auth</c> matching by level; other built-ins fall back
/// to "type match only". Custom guard kinds can plug in their own match predicates by
/// registering an <see cref="Guards.IIvrGuardFactory"/> whose produced guard implements
/// equality.
/// </remarks>
public sealed class IvrAuthResolverDocument
{
    /// <summary>Guard pattern this resolver satisfies. Required.</summary>
    [YamlMember(Alias = "guard")]
    public IvrGuardDocument Guard { get; set; } = new();

    /// <summary>Id of the sub-workflow to push when the guard fails. Required.</summary>
    [YamlMember(Alias = "subflow")]
    public string Subflow { get; set; } = string.Empty;

    /// <summary>Optional lower-bound version constraint passed to the catalog at push time.</summary>
    [YamlMember(Alias = "minVersion")]
    public int? MinVersion { get; set; }

    /// <summary>Optional upper-bound version constraint.</summary>
    [YamlMember(Alias = "maxVersion")]
    public int? MaxVersion { get; set; }
}
