using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Definition;

/// <summary>
/// A reusable IVR capability — a discrete business task the IVR can perform
/// (balance lookup, card activation, agent transfer). Stages reference capabilities
/// by <see cref="Id"/>.
/// </summary>
public sealed class IvrCapabilityDocument
{
    /// <summary>Capability identifier (e.g. <c>balance.lookup</c>).</summary>
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable summary used in tool descriptions and routing prompts.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>Preconditions that must hold before the capability can be invoked.</summary>
    [YamlMember(Alias = "requires")]
    public List<IvrGuardDocument> Requires { get; set; } = [];

    /// <summary>Tool names exposed to the agent/strategy while this capability is active.</summary>
    [YamlMember(Alias = "tools")]
    public List<string> Tools { get; set; } = [];

    /// <summary>Optional ToolUsageRule overrides for the tools in <see cref="Tools"/>.</summary>
    [YamlMember(Alias = "toolRules")]
    public List<IvrToolRuleDocument> ToolRules { get; set; } = [];
}
