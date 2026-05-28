using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Definition;

/// <summary>
/// Root POCO deserialized from an IVR workflow YAML document. This is the raw
/// shape produced by <c>YamlDotNet</c>; semantic validation and lowering to the
/// runtime <see cref="RealtimeIvrWorkflowDefinition"/> happens in the compiler.
/// </summary>
public sealed class IvrWorkflowDocument
{
    /// <summary>Workflow identifier. Required.</summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional workflow schema version. Reserved for forward compatibility.</summary>
    [YamlMember(Alias = "version")]
    public int Version { get; set; } = 1;

    /// <summary>Human-readable description of the workflow's purpose.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>Root-level strategy declaration controlling tier selection and fallback.</summary>
    [YamlMember(Alias = "strategy")]
    public IvrStrategyDocument? Strategy { get; set; }

    /// <summary>Shared base prompt and common tools applied to every stage.</summary>
    [YamlMember(Alias = "base")]
    public IvrBaseDocument? Base { get; set; }

    /// <summary>Reusable capabilities the IVR can fulfill (e.g., balance.lookup).</summary>
    [YamlMember(Alias = "capabilities")]
    public List<IvrCapabilityDocument> Capabilities { get; set; } = [];

    /// <summary>Ordered stages composing the workflow. The first stage is the initial stage.</summary>
    [YamlMember(Alias = "stages")]
    public List<IvrStageDocument> Stages { get; set; } = [];

    /// <summary>
    /// Phase 3: workflow-level table mapping guard patterns to sub-workflows that
    /// satisfy them. The navigator consults this list whenever a transition's or stage's
    /// <c>requires:</c> fail, pushes the first matching resolver's subflow with the
    /// original target as the return step, and re-evaluates after pop.
    /// </summary>
    [YamlMember(Alias = "authResolvers")]
    public List<IvrAuthResolverDocument> AuthResolvers { get; set; } = [];

    /// <summary>
    /// Phase 3: workflow-default stage to enter when a transition's guards cannot be
    /// satisfied by any resolver chain (no resolver match, or the resolver subflow
    /// itself terminates in failure). Per-stage <c>onUnauthorized</c> overrides this.
    /// When neither is set the call ends.
    /// </summary>
    [YamlMember(Alias = "onUnauthorized")]
    public string? OnUnauthorized { get; set; }
}
