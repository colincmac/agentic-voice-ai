using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Definition;

/// <summary>
/// YAML projection of <see cref="StepNluConfiguration"/>. Declares the non-generative
/// natural-language understanding (NLU) configuration for a stage. Mirrors
/// <see cref="IvrDtmfDocument"/>'s prompt surface so NLU stages can carry SSML / audio
/// overrides on equal footing with DTMF stages, plus NLU-specific knobs (confidence
/// threshold, no-match / no-input policy, classifier examples, and an optional
/// stage-scoped intent list that overrides the stage's root-level <c>intents</c>).
/// </summary>
/// <remarks>
/// Prompt fields follow the same dual-channel convention as <see cref="IvrDtmfDocument"/>:
/// each logical prompt has a <c>...Prompt</c> (SSML or plain text, synthesized via
/// the configured speech synthesizer) and an <c>...AudioFile</c> (pre-recorded audio
/// played in lieu of synthesis). When both are supplied for the same logical prompt,
/// the audio file takes precedence.
/// </remarks>
public sealed class IvrNluDocument
{
    /// <summary>Greeting / instruction prompt spoken when the NLU stage is entered. SSML is supported.</summary>
    [YamlMember(Alias = "ssmlPrompt")]
    public string? SsmlPrompt { get; set; }

    /// <summary>Pre-recorded audio URI played instead of synthesizing <see cref="SsmlPrompt"/>.</summary>
    [YamlMember(Alias = "audioFile")]
    public string? AudioFile { get; set; }

    /// <summary>Prompt spoken when no intent matches above the configured confidence threshold.</summary>
    [YamlMember(Alias = "onNoMatchPrompt")]
    public string? OnNoMatchPrompt { get; set; }

    /// <summary>Pre-recorded audio played on no-match (alternative to <see cref="OnNoMatchPrompt"/>).</summary>
    [YamlMember(Alias = "onNoMatchAudioFile")]
    public string? OnNoMatchAudioFile { get; set; }

    /// <summary>Prompt spoken when the caller is silent past the listening timeout.</summary>
    [YamlMember(Alias = "onNoInputPrompt")]
    public string? OnNoInputPrompt { get; set; }

    /// <summary>Pre-recorded audio played on no-input (alternative to <see cref="OnNoInputPrompt"/>).</summary>
    [YamlMember(Alias = "onNoInputAudioFile")]
    public string? OnNoInputAudioFile { get; set; }

    /// <summary>Prompt spoken when the strategy confirms a classified intent before transitioning.</summary>
    [YamlMember(Alias = "onConfirmPrompt")]
    public string? OnConfirmPrompt { get; set; }

    /// <summary>Pre-recorded audio played on confirmation (alternative to <see cref="OnConfirmPrompt"/>).</summary>
    [YamlMember(Alias = "onConfirmAudioFile")]
    public string? OnConfirmAudioFile { get; set; }

    /// <summary>Prompt spoken when the NLU stage hands off (e.g. to a DTMF fallback or human agent).</summary>
    [YamlMember(Alias = "onHandoffPrompt")]
    public string? OnHandoffPrompt { get; set; }

    /// <summary>Pre-recorded audio played on handoff (alternative to <see cref="OnHandoffPrompt"/>).</summary>
    [YamlMember(Alias = "onHandoffAudioFile")]
    public string? OnHandoffAudioFile { get; set; }

    /// <summary>Maximum consecutive no-match events before the stage escalates or hands off. Defaults to <c>2</c>.</summary>
    [YamlMember(Alias = "maxNoMatch")]
    public int MaxNoMatch { get; set; } = 2;

    /// <summary>Maximum consecutive no-input events before the stage escalates or hands off. Defaults to <c>2</c>.</summary>
    [YamlMember(Alias = "maxNoInput")]
    public int MaxNoInput { get; set; } = 2;

    /// <summary>Minimum classifier confidence (<c>0.0</c>-<c>1.0</c>) for a match to be accepted. Defaults to <c>0.5</c>.</summary>
    [YamlMember(Alias = "confidenceThreshold")]
    public double ConfidenceThreshold { get; set; } = 0.5;

    /// <summary>Listening window before a no-input event fires.</summary>
    [YamlMember(Alias = "noInputTimeoutMs")]
    public int NoInputTimeoutMs { get; set; } = 5000;

    /// <summary>Seed utterances passed to the classifier to bias NLU toward the expected wording for this stage.</summary>
    [YamlMember(Alias = "examples")]
    public List<string> Examples { get; set; } = [];

    /// <summary>
    /// Optional stage-scoped intent list. When present, the NLU tier classifies against
    /// these instead of (or in addition to) the stage's root-level <c>intents</c>. Same
    /// shape as <see cref="IvrIntentDocument"/>.
    /// </summary>
    [YamlMember(Alias = "intents")]
    public List<IvrIntentDocument> Intents { get; set; } = [];
}
