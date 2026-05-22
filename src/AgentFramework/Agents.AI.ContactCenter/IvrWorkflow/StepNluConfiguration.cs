using System;
using System.Collections.Generic;

namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// Runtime configuration for the non-generative NLU (intent-recognition) tier of a
/// workflow stage. Parallel in spirit to <see cref="StepDtmfConfiguration"/>: holds
/// the resolved SSML / audio prompt overrides and policy values lowered from
/// <c>IvrNluDocument</c> by the compilation pipeline. Consumed by
/// <c>NluConversationStrategy</c> to drive speech synthesis and classification.
/// </summary>
/// <remarks>
/// Every prompt has a paired <c>...AudioFile</c>; when both are populated the audio
/// file takes precedence to allow pre-recorded brand voices to override the
/// synthesizer.
/// </remarks>
public sealed class StepNluConfiguration
{
    /// <summary>Greeting / instruction prompt spoken on stage entry. May contain SSML.</summary>
    public string? SsmlPromptOverride { get; set; }

    /// <summary>Pre-recorded audio for stage entry (overrides <see cref="SsmlPromptOverride"/>).</summary>
    public Uri? AudioFile { get; set; }

    /// <summary>Prompt spoken when no intent matches above <see cref="ConfidenceThreshold"/>.</summary>
    public string? OnNoMatchPrompt { get; set; }

    /// <summary>Pre-recorded audio played on no-match.</summary>
    public Uri? OnNoMatchAudioFile { get; set; }

    /// <summary>Prompt spoken when the caller is silent past <see cref="NoInputTimeout"/>.</summary>
    public string? OnNoInputPrompt { get; set; }

    /// <summary>Pre-recorded audio played on no-input.</summary>
    public Uri? OnNoInputAudioFile { get; set; }

    /// <summary>Prompt spoken to confirm a classified intent before transitioning.</summary>
    public string? OnConfirmPrompt { get; set; }

    /// <summary>Pre-recorded audio played on confirmation.</summary>
    public Uri? OnConfirmAudioFile { get; set; }

    /// <summary>Prompt spoken when the stage hands off (e.g. to DTMF fallback or a human agent).</summary>
    public string? OnHandoffPrompt { get; set; }

    /// <summary>Pre-recorded audio played on handoff.</summary>
    public Uri? OnHandoffAudioFile { get; set; }

    /// <summary>Maximum consecutive no-match events before the stage escalates / hands off.</summary>
    public int MaxNoMatch { get; set; } = 2;

    /// <summary>Maximum consecutive no-input events before the stage escalates / hands off.</summary>
    public int MaxNoInput { get; set; } = 2;

    /// <summary>Minimum classifier confidence (0.0-1.0) for a match to be accepted.</summary>
    public double ConfidenceThreshold { get; set; } = 0.5;

    /// <summary>Window the strategy waits for caller speech before raising a no-input event.</summary>
    public TimeSpan NoInputTimeout { get; set; } = TimeSpan.FromMilliseconds(5000);

    /// <summary>Seed utterances biasing the classifier toward expected wording for this stage.</summary>
    public IReadOnlyList<string> Examples { get; set; } = [];

    /// <summary>
    /// Stage-scoped intent map (intent name → next stage id) lowered from the NLU
    /// document. When empty, the strategy falls back to the stage's root intent list.
    /// </summary>
    public IReadOnlyDictionary<string, string> IntentTransitions { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
