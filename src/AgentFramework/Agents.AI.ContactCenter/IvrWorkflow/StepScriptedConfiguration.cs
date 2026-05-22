using System;
using System.Collections.Generic;

namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// Runtime configuration for the scripted (non-generative) tiers of an IVR workflow stage.
/// Holds the resolved prompt surface and control knobs shared by the DTMF and NLU tiers,
/// plus optional per-tier sub-configurations under <see cref="Nlu"/> and <see cref="Dtmf"/>
/// for the values that genuinely differ between the two tiers.
/// </summary>
/// <remarks>
/// <para>
/// Produced by the IVR compiler from a <c>IvrScriptedStageDocument</c>. Consumed by
/// <c>NluConversationStrategy</c> and the DTMF strategies via
/// <see cref="RealtimeIvrWorkflowStep.StepScriptedConfiguration"/>.
/// </para>
/// <para>
/// Prompt resolution: tier-specific override (<see cref="StepNluConfiguration.SsmlPromptOverride"/>
/// / <see cref="StepDtmfConfiguration.SsmlPromptOverride"/>) takes precedence over the shared
/// values stored on this object. For paired <c>...Prompt</c> / <c>...AudioFile</c> slots, when
/// both are populated the audio file wins at runtime.
/// </para>
/// </remarks>
public sealed class StepScriptedConfiguration
{
    /// <summary>Shared stage entry prompt (SSML or plain text). May be overridden by a tier sub-config.</summary>
    public string? SsmlPrompt { get; init; }

    /// <summary>Shared stage entry audio. May be overridden by a tier sub-config.</summary>
    public Uri? AudioFile { get; init; }

    /// <summary>Prompt spoken when input is rejected (DTMF invalid digit / NLU no-match).</summary>
    public string? OnErrorPrompt { get; init; }

    /// <summary>Audio played on input rejection.</summary>
    public Uri? OnErrorAudioFile { get; init; }

    /// <summary>Prompt spoken when the caller is silent past <see cref="NoInputTimeout"/>.</summary>
    public string? OnNoInputPrompt { get; init; }

    /// <summary>Audio played on no-input.</summary>
    public Uri? OnNoInputAudioFile { get; init; }

    /// <summary>Prompt spoken when the stage hands off (e.g. to a fallback tier or a human agent).</summary>
    public string? OnHandoffPrompt { get; init; }

    /// <summary>Audio played on handoff.</summary>
    public Uri? OnHandoffAudioFile { get; init; }

    /// <summary>Prompt spoken to confirm a classified selection before transitioning.</summary>
    public string? OnConfirmPrompt { get; init; }

    /// <summary>Audio played on confirmation.</summary>
    public Uri? OnConfirmAudioFile { get; init; }

    /// <summary>Maximum consecutive no-match events before the stage escalates / hands off.</summary>
    public int MaxNoMatch { get; init; } = 2;

    /// <summary>Maximum consecutive no-input events before the stage escalates / hands off.</summary>
    public int MaxNoInput { get; init; } = 2;

    /// <summary>Window the strategy waits for caller speech before raising a no-input event.</summary>
    public TimeSpan NoInputTimeout { get; init; } = TimeSpan.FromMilliseconds(5000);

    /// <summary>
    /// Minimum NLU classifier confidence (<c>0.0</c>-<c>1.0</c>) for a match to be accepted.
    /// Ignored by the DTMF tier.
    /// </summary>
    public double ConfidenceThreshold { get; init; } = 0.5;

    /// <summary>Seed utterances biasing the NLU classifier toward expected wording. Ignored by DTMF.</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];

    /// <summary>NLU-tier sub-configuration. <see langword="null"/> when the stage has no NLU presence.</summary>
    public StepNluConfiguration? Nlu { get; init; }

    /// <summary>DTMF-tier sub-configuration. <see langword="null"/> when the stage has no DTMF presence.</summary>
    public StepDtmfConfiguration? Dtmf { get; init; }
}
