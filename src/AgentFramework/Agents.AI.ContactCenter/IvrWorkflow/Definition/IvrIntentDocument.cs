using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Definition;

/// <summary>
/// Caller intent declaration. May resolve to a capability invocation, a direct stage
/// transition, or both. Intents may be declared at the workflow root (reusable) or
/// nested in a stage (locally-scoped).
/// </summary>
public sealed class IvrIntentDocument
{
    /// <summary>Intent name (e.g. <c>balance</c>).</summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Example utterances used by the default keyword intent classifier and to seed NLU.</summary>
    [YamlMember(Alias = "examples")]
    public List<string> Examples { get; set; } = [];

    /// <summary>Stage to transition to when this intent is detected. Optional.</summary>
    [YamlMember(Alias = "nextStage")]
    public string? NextStage { get; set; }

    /// <summary>Capability id to invoke when this intent is detected. Optional.</summary>
    [YamlMember(Alias = "capability")]
    public string? Capability { get; set; }

    /// <summary>Optional confirmation prompt before transition/invocation.</summary>
    [YamlMember(Alias = "confirmPrompt")]
    public string? ConfirmPrompt { get; set; }
}
