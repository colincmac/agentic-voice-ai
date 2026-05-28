using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Definition;

/// <summary>
/// DTMF-tier override sub-block nested under <see cref="IvrScriptedStageDocument"/>.
/// Carries only the values that are unique to DTMF — an optional entry-prompt override
/// (e.g. "press 1 for balance" vs. the NLU "say balance"), the menu <c>options</c>
/// binding digits to actions, and the digit <c>collect</c> block (PIN / account-number
/// entry). Shared prompts (no-input / handoff / confirm / generic error) and policy knobs
/// live on the parent <see cref="IvrScriptedStageDocument"/>.
/// </summary>
/// <remarks>
/// When both <see cref="SsmlPrompt"/> and <see cref="AudioFile"/> are populated the audio
/// file takes precedence at runtime.
/// </remarks>
public sealed class IvrDtmfDocument
{
    /// <summary>DTMF-tier entry prompt override. Falls back to <see cref="IvrScriptedStageDocument.SsmlPrompt"/>.</summary>
    [YamlMember(Alias = "ssmlPrompt")]
    public string? SsmlPrompt { get; set; }

    /// <summary>DTMF-tier entry audio override. Falls back to <see cref="IvrScriptedStageDocument.AudioFile"/>.</summary>
    [YamlMember(Alias = "audioFile")]
    public string? AudioFile { get; set; }

    /// <summary>Per-digit menu options. Each entry binds a digit to an intent, capability, transition, or tool.</summary>
    [YamlMember(Alias = "options")]
    public List<IvrDtmfOptionDocument> Options { get; set; } = [];

    /// <summary>Digit-collection (e.g. PIN, account number) block. When set, the stage collects a digit buffer.</summary>
    [YamlMember(Alias = "collect")]
    public IvrDtmfCollectionDocument? Collect { get; set; }
}

/// <summary>
/// Binds a DTMF digit (or <c>*</c>/<c>#</c>) to a routing decision. Exactly one of
/// <see cref="Intent"/>, <see cref="Capability"/>, <see cref="NextStage"/>, or
/// <see cref="Tool"/> should be set.
/// </summary>
public sealed class IvrDtmfOptionDocument
{
    [YamlMember(Alias = "digit")]
    public string Digit { get; set; } = string.Empty;

    [YamlMember(Alias = "label")]
    public string Label { get; set; } = string.Empty;

    [YamlMember(Alias = "intent")]
    public string? Intent { get; set; }

    [YamlMember(Alias = "capability")]
    public string? Capability { get; set; }

    [YamlMember(Alias = "nextStage")]
    public string? NextStage { get; set; }

    [YamlMember(Alias = "tool")]
    public string? Tool { get; set; }

    [YamlMember(Alias = "args")]
    public Dictionary<string, object?> Args { get; set; } = [];

    [YamlMember(Alias = "onFailurePrompt")]
    public string? OnFailurePrompt { get; set; }

    [YamlMember(Alias = "onFailureAudioFile")]
    public string? OnFailureAudioFile { get; set; }
}

/// <summary>Configures the digit-collection (buffered) DTMF flow for a stage.</summary>
public sealed class IvrDtmfCollectionDocument
{
    [YamlMember(Alias = "minDigits")]
    public int MinDigits { get; set; } = 1;

    [YamlMember(Alias = "maxDigits")]
    public int MaxDigits { get; set; } = 1;

    [YamlMember(Alias = "terminator")]
    public string Terminator { get; set; } = "#";

    [YamlMember(Alias = "interDigitTimeoutMs")]
    public int InterDigitTimeoutMs { get; set; } = 5000;

    /// <summary>Tool name resolved through <see cref="Registry.IIvrToolRegistry"/>.</summary>
    [YamlMember(Alias = "validator")]
    public string? Validator { get; set; }

    /// <summary>Argument name the validator expects to receive the digit buffer in.</summary>
    [YamlMember(Alias = "digitsParameterName")]
    public string DigitsParameterName { get; set; } = "digits";

    /// <summary>Bound arguments passed to the validator alongside the collected digits.</summary>
    [YamlMember(Alias = "args")]
    public Dictionary<string, object?> Args { get; set; } = [];

    /// <summary>State key the collected buffer is stored under on success.</summary>
    [YamlMember(Alias = "collectedStateKey")]
    public string? CollectedStateKey { get; set; }

    /// <summary>Next stage on successful validation.</summary>
    [YamlMember(Alias = "onValidNextStage")]
    public string? OnValidNextStage { get; set; }

    [YamlMember(Alias = "onInvalidPrompt")]
    public string? OnInvalidPrompt { get; set; }

    [YamlMember(Alias = "onInvalidAudioFile")]
    public string? OnInvalidAudioFile { get; set; }
}
