using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Definition;

/// <summary>
/// YAML strategy declaration. Specifies which interaction modes a workflow (or stage)
/// supports, in priority order, plus which tiers should be pre-warmed when the
/// workflow loads.
/// </summary>
public sealed class IvrStrategyDocument
{
    /// <summary>Primary interaction mode (e.g. <c>realtime</c>, <c>nlu</c>, <c>dtmf</c>, <c>mixed</c>).</summary>
    [YamlMember(Alias = "primary")]
    public string Primary { get; set; } = "realtime";

    /// <summary>Ordered list of fallback modes if <see cref="Primary"/> is unavailable or fails mid-call.</summary>
    [YamlMember(Alias = "fallback")]
    public List<string> Fallback { get; set; } = [];

    /// <summary>Tiers/modes the host should pre-warm so they are ready for fast cut-over.</summary>
    [YamlMember(Alias = "prewarmTiers")]
    public List<string> PrewarmTiers { get; set; } = [];

    /// <summary>When true, a running call can be degraded to a lower tier if the active tier fails.</summary>
    [YamlMember(Alias = "allowMidCallDegradation")]
    public bool AllowMidCallDegradation { get; set; } = true;
}
