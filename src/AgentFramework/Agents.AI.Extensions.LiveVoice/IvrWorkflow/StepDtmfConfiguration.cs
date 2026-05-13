using System;
using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.LiveVoice.IvrWorkflow;

public sealed class StepDtmfConfiguration(
    char terminationDigit = '#',
    Dictionary<char, string>? options = null,
    int interDigitTimeoutMs = 5000,
    int minNumberOfDigits = 1,
    int maxNumberOfDigits = 1,
    string? promptOverride = null)
{

    public Uri? AudioFile { get; set; } = null;
    public Uri? OnErrorAudioFile { get; set; } = null;
    public string? OnErrorPrompt { get; set; } = null;

    public string? PromptOverride { get; set; } = promptOverride;

    // If using a DTMF menu, the keys are the digits to press, and the values are descriptions of the option (e.g. "For sales, press 1")
    public Dictionary<char, string>? Options = options;

    /// <summary>
    /// Rich per-digit option bindings used when an option needs to invoke a tool
    /// (e.g. "press 0 to talk to a live agent") or has a custom failure prompt.
    /// When present, this dictionary takes precedence over <see cref="Options"/>.
    /// Labels in <see cref="Options"/> are still used to render the menu prompt
    /// for digits that don't have a <see cref="DtmfMenuOption"/> binding.
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
