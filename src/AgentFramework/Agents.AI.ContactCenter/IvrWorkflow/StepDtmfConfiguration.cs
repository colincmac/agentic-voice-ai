using System;
using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// DTMF-tier sub-configuration nested under <see cref="StepScriptedConfiguration"/>.
/// Carries the values that are unique to DTMF — an optional entry-prompt override
/// (e.g. "press 1 for balance"), the menu options, and the digit-collection block.
/// Shared prompts (error / no-input / handoff / confirm) and counters live on the parent
/// <see cref="StepScriptedConfiguration"/>.
/// </summary>
public sealed class StepDtmfConfiguration(
    char terminationDigit = '#',
    int interDigitTimeoutMs = 5000,
    int minNumberOfDigits = 1,
    int maxNumberOfDigits = 1,
    string? promptOverride = null)
{
    /// <summary>DTMF-tier entry audio override. Falls back to <see cref="StepScriptedConfiguration.AudioFile"/>.</summary>
    public Uri? AudioFile { get; set; } = null;

    /// <summary>DTMF-tier entry prompt override (SSML or plain). Falls back to <see cref="StepScriptedConfiguration.SsmlPrompt"/>.</summary>
    public string? SsmlPromptOverride { get; set; } = promptOverride;

    /// <summary>
    /// Per-digit menu bindings. Each entry maps a DTMF digit to a
    /// <see cref="DtmfMenuOption"/> describing the label spoken in the menu prompt
    /// and the action taken when the digit is pressed (a transition, a tool
    /// invocation, or both).
    /// </summary>
    public IReadOnlyDictionary<char, DtmfMenuOption>? MenuOptions { get; set; }

    /// <summary>
    /// Tool invoked after the digit buffer is terminated or full, when the step is
    /// collecting a sequence of digits (e.g. an account number). The strategy injects
    /// the collected digits as an argument named <see cref="DigitsParameterName"/>
    /// alongside any arguments in <see cref="DigitCollectionArguments"/>, then
    /// interprets the return value with the same conventions as menu options.
    /// </summary>
    public AITool? DigitCollectionValidator { get; set; }

    /// <summary>Argument name used to pass the collected digit string to <see cref="DigitCollectionValidator"/>.</summary>
    public string DigitsParameterName { get; set; } = "digits";

    /// <summary>Additional bound arguments passed to <see cref="DigitCollectionValidator"/>.</summary>
    public IReadOnlyDictionary<string, object?>? DigitCollectionArguments { get; set; }

    /// <summary>
    /// State key under which the collected digits are stored when the validator reports
    /// success. Defaults to <c>"{stepId}_collected"</c> when null.
    /// </summary>
    public string? CollectedStateKey { get; set; }

    /// <summary>Step to transition to after the validator returns success.</summary>
    public string? OnValidNextStepId { get; set; }

    /// <summary>Prompt spoken when the validator returns failure.</summary>
    public string? OnInvalidPrompt { get; set; }

    /// <summary>Audio file played when the validator returns failure.</summary>
    public Uri? OnInvalidAudioFile { get; set; }

    // If entering a sequence of digits (e.g. account number), this is the prompt played after each digit is entered, describing what to enter (e.g. "Please enter your 5-digit account number, followed by the pound key")
    public char TerminationDigitChar = terminationDigit;
    public int MinNumberOfDigits = minNumberOfDigits;
    public int MaxNumberOfDigits = maxNumberOfDigits;

    public int InterDigitTimeoutMs = interDigitTimeoutMs;
}
