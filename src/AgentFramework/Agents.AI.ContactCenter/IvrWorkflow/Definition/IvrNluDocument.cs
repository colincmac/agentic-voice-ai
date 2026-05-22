using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Definition;

/// <summary>
/// NLU-tier override sub-block nested under <see cref="IvrScriptedStageDocument"/>.
/// Carries only the values that differ from the shared <c>scripted</c> defaults — namely
/// an optional entry-prompt override (e.g. "say balance" vs. the DTMF "press 1") and an
/// optional stage-scoped intent list. All shared knobs (no-match / no-input / handoff /
/// confirm prompts, retry counters, confidence threshold, classifier examples) live on
/// the parent <see cref="IvrScriptedStageDocument"/>.
/// </summary>
/// <remarks>
/// When both <see cref="SsmlPrompt"/> and <see cref="AudioFile"/> are populated the audio
/// file takes precedence at runtime.
/// </remarks>
public sealed class IvrNluDocument
{
    /// <summary>NLU-tier entry prompt override. Falls back to <see cref="IvrScriptedStageDocument.SsmlPrompt"/>.</summary>
    [YamlMember(Alias = "ssmlPrompt")]
    public string? SsmlPrompt { get; set; }

    /// <summary>NLU-tier entry audio override. Falls back to <see cref="IvrScriptedStageDocument.AudioFile"/>.</summary>
    [YamlMember(Alias = "audioFile")]
    public string? AudioFile { get; set; }

    /// <summary>
    /// Optional stage-scoped intent list. When present, the NLU tier classifies against
    /// these instead of (or in addition to) the stage's root-level <c>intents</c>. Same
    /// shape as <see cref="IvrIntentDocument"/>.
    /// </summary>
    [YamlMember(Alias = "intents")]
    public List<IvrIntentDocument> Intents { get; set; } = [];
}
