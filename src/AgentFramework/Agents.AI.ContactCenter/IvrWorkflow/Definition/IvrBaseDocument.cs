using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Definition;

/// <summary>
/// Shared base section applied to every stage in the workflow.
/// </summary>
public sealed class IvrBaseDocument
{
    /// <summary>Base realtime prompt configuration (role, personality, context, pronunciations, safety).</summary>
    [YamlMember(Alias = "prompt")]
    public IvrPromptDocument? Prompt { get; set; }

    /// <summary>Tool names (resolved through <see cref="Registry.IIvrToolRegistry"/>) available on every stage.</summary>
    [YamlMember(Alias = "commonTools")]
    public List<string> CommonTools { get; set; } = [];

    /// <summary>Default authentication level required to enter any stage. Overridden by per-stage requirements.</summary>
    [YamlMember(Alias = "requiredAuthLevel")]
    public string? RequiredAuthLevel { get; set; }
}
