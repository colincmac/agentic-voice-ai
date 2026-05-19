using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Definition;

/// <summary>
/// YAML projection of <see cref="Agents.AI.Extensions.RealtimeAgentHelpers.Prompting.ToolUsageRule"/>.
/// </summary>
public sealed class IvrToolRuleDocument
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "useWhen")]
    public string UseWhen { get; set; } = string.Empty;

    [YamlMember(Alias = "doNotUseWhen")]
    public string? DoNotUseWhen { get; set; }

    /// <summary>One of <c>proactive</c>, <c>confirmationFirst</c>, <c>preambleFirst</c>.</summary>
    [YamlMember(Alias = "behavior")]
    public string? Behavior { get; set; }

    [YamlMember(Alias = "preamblePhrases")]
    public List<string> PreamblePhrases { get; set; } = [];

    [YamlMember(Alias = "confirmationPhrase")]
    public string? ConfirmationPhrase { get; set; }
}
