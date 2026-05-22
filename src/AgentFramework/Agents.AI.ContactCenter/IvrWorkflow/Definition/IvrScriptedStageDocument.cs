using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Definition;

/// <summary>
/// Shared <b>scripted</b> stage configuration covering the non-generative tiers (DTMF and NLU).
/// Hosts the prompts and control knobs that both tiers can share, plus optional tier-specific
/// override sub-blocks under <see cref="Nlu"/> and <see cref="Dtmf"/>.
/// </summary>
/// <remarks>
/// <para>
/// The IVR workflow distinguishes two categories of stage behavior:
/// </para>
/// <list type="bullet">
///   <item><b>Generative AI</b> — driven by an LLM/Realtime voice agent. Configured under the
///   stage's <c>realtime:</c> block (<see cref="IvrRealtimeStageDocument"/>).</item>
///   <item><b>Scripted</b> — deterministic IVR flows (DTMF menus and non-generative NLU
///   classification). Configured here, under the stage's <c>scripted:</c> block.</item>
/// </list>
/// <para>
/// Because the two scripted tiers normally share the same prompt content (greeting, error,
/// no-input, handoff, confirm) and policy knobs (retry counts, timeouts, classifier
/// thresholds), those values live on this parent document. The per-tier sub-blocks only
/// carry the values that genuinely differ — for example a DTMF "press 1 for balance"
/// prompt vs. an NLU "say balance" prompt, the DTMF menu <c>options</c> / digit
/// <c>collect</c> block, and the NLU intent table.
/// </para>
/// <para>
/// <b>Prompt resolution precedence (lowest to highest priority):</b>
/// <c>null</c> → shared <c>scripted</c> value → tier-specific override (<c>scripted.nlu</c>
/// or <c>scripted.dtmf</c>). For paired <c>...Prompt</c> / <c>...AudioFile</c> slots, when
/// both are populated the audio file wins at runtime.
/// </para>
/// </remarks>
public sealed class IvrScriptedStageDocument
{
    /// <summary>Stage entry prompt (SSML or plain text). Used by both tiers unless overridden in the tier sub-block.</summary>
    [YamlMember(Alias = "ssmlPrompt")]
    public string? SsmlPrompt { get; set; }

    /// <summary>Pre-recorded audio for the stage entry (alternative to <see cref="SsmlPrompt"/>).</summary>
    [YamlMember(Alias = "audioFile")]
    public string? AudioFile { get; set; }

    /// <summary>Prompt spoken when input is rejected (DTMF invalid digit / NLU no-match).</summary>
    [YamlMember(Alias = "onErrorPrompt")]
    public string? OnErrorPrompt { get; set; }

    /// <summary>Pre-recorded audio played on error (alternative to <see cref="OnErrorPrompt"/>).</summary>
    [YamlMember(Alias = "onErrorAudioFile")]
    public string? OnErrorAudioFile { get; set; }

    /// <summary>Prompt spoken when the caller is silent past <see cref="NoInputTimeoutMs"/>.</summary>
    [YamlMember(Alias = "onNoInputPrompt")]
    public string? OnNoInputPrompt { get; set; }

    /// <summary>Pre-recorded audio played on no-input.</summary>
    [YamlMember(Alias = "onNoInputAudioFile")]
    public string? OnNoInputAudioFile { get; set; }

    /// <summary>Prompt spoken when the stage hands off (e.g. to a fallback tier or a human agent).</summary>
    [YamlMember(Alias = "onHandoffPrompt")]
    public string? OnHandoffPrompt { get; set; }

    /// <summary>Pre-recorded audio played on handoff.</summary>
    [YamlMember(Alias = "onHandoffAudioFile")]
    public string? OnHandoffAudioFile { get; set; }

    /// <summary>Prompt spoken to confirm a classified selection before transitioning.</summary>
    [YamlMember(Alias = "onConfirmPrompt")]
    public string? OnConfirmPrompt { get; set; }

    /// <summary>Pre-recorded audio played on confirmation.</summary>
    [YamlMember(Alias = "onConfirmAudioFile")]
    public string? OnConfirmAudioFile { get; set; }

    /// <summary>Maximum consecutive no-match events before the stage escalates or hands off. Defaults to <c>2</c>.</summary>
    [YamlMember(Alias = "maxNoMatch")]
    public int MaxNoMatch { get; set; } = 2;

    /// <summary>Maximum consecutive no-input events before the stage escalates or hands off. Defaults to <c>2</c>.</summary>
    [YamlMember(Alias = "maxNoInput")]
    public int MaxNoInput { get; set; } = 2;

    /// <summary>Listening window before a no-input event fires. Defaults to <c>5000</c>ms.</summary>
    [YamlMember(Alias = "noInputTimeoutMs")]
    public int NoInputTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Minimum NLU classifier confidence (<c>0.0</c>-<c>1.0</c>) for a match to be accepted.
    /// Ignored by the DTMF tier. Defaults to <c>0.5</c>.
    /// </summary>
    [YamlMember(Alias = "confidenceThreshold")]
    public double ConfidenceThreshold { get; set; } = 0.5;

    /// <summary>
    /// Seed utterances passed to the NLU classifier to bias it toward expected wording.
    /// Ignored by the DTMF tier.
    /// </summary>
    [YamlMember(Alias = "examples")]
    public List<string> Examples { get; set; } = [];

    /// <summary>Optional NLU-specific overrides (entry prompt override, stage-scoped intent list).</summary>
    [YamlMember(Alias = "nlu")]
    public IvrNluDocument? Nlu { get; set; }

    /// <summary>Optional DTMF-specific overrides (entry prompt override, menu <c>options</c>, digit <c>collect</c>).</summary>
    [YamlMember(Alias = "dtmf")]
    public IvrDtmfDocument? Dtmf { get; set; }
}
