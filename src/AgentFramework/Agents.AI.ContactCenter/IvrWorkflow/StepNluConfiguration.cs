using System.Collections.Generic;

namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// NLU-tier sub-configuration nested under <see cref="StepScriptedConfiguration"/>.
/// Carries the values that are unique to the NLU tier — an optional entry-prompt
/// override (e.g. "say balance") and the stage-scoped intent transition map. All shared
/// prompts and policy knobs (no-match / no-input / handoff / confirm, retry counters,
/// confidence threshold, classifier examples) live on the parent
/// <see cref="StepScriptedConfiguration"/>.
/// </summary>
public sealed class StepNluConfiguration
{
    /// <summary>NLU-tier entry prompt override. Falls back to <see cref="StepScriptedConfiguration.SsmlPrompt"/>.</summary>
    public string? SsmlPromptOverride { get; set; }

    /// <summary>NLU-tier entry audio override. Falls back to <see cref="StepScriptedConfiguration.AudioFile"/>.</summary>
    public System.Uri? AudioFile { get; set; }

    /// <summary>
    /// Stage-scoped intent map (intent name → next stage id) lowered from the NLU
    /// document. When empty, the strategy falls back to the stage's root intent list.
    /// </summary>
    public IReadOnlyDictionary<string, string> IntentTransitions { get; set; } =
        new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
}

