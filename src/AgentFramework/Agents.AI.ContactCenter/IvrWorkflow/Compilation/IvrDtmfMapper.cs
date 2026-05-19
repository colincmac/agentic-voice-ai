using System;
using System.Collections.Generic;
using System.Linq;
using Agents.AI.ContactCenter.IvrWorkflow.Definition;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>
/// Lowers <see cref="IvrDtmfDocument"/> into the runtime
/// <see cref="StepDtmfConfiguration"/> shape consumed by the DTMF strategies.
/// </summary>
internal static class IvrDtmfMapper
{
    public static StepDtmfConfiguration Map(IvrDtmfDocument doc, string stageId, List<string> errors)
    {
        var collect = doc.Collect;
        var min = collect?.MinDigits ?? 1;
        var max = collect?.MaxDigits ?? Math.Max(1, doc.Options.Count > 0 ? 1 : min);
        var terminator = ParseDigit(collect?.Terminator, '#', stageId, "collect.terminator", errors);
        var interDigit = collect?.InterDigitTimeoutMs ?? 5000;

        var menu = doc.Options.Count == 0 ? null : BuildMenu(doc, stageId, errors);

        var config = new StepDtmfConfiguration(
            terminationDigit: terminator,
            interDigitTimeoutMs: interDigit,
            minNumberOfDigits: min,
            maxNumberOfDigits: max,
            promptOverride: doc.SsmlPrompt)
        {
            AudioFile = TryParseUri(doc.AudioFile, stageId, "audioFile", errors),
            OnErrorPrompt = doc.OnErrorPrompt,
            OnErrorAudioFile = TryParseUri(doc.OnErrorAudioFile, stageId, "onErrorAudioFile", errors),
            MenuOptions = menu,
        };

        if (collect is not null)
        {
            config.DigitsParameterName = string.IsNullOrWhiteSpace(collect.DigitsParameterName)
                ? "digits"
                : collect.DigitsParameterName;
            config.DigitCollectionArguments = collect.Args.Count == 0 ? null : collect.Args;
            config.CollectedStateKey = collect.CollectedStateKey;
            config.OnValidNextStepId = collect.OnValidNextStage;
            config.OnInvalidPrompt = collect.OnInvalidPrompt;
            config.OnInvalidAudioFile = TryParseUri(collect.OnInvalidAudioFile, stageId, "collect.onInvalidAudioFile", errors);
            // DigitCollectionValidator is resolved lazily by the runtime via the supplied name; we leave it null here
            // because the legacy StepDtmfConfiguration expects an AITool instance. The validator name is preserved
            // on a side channel: stored under DigitCollectionArguments["__validator"] for the strategy to resolve.
            if (!string.IsNullOrWhiteSpace(collect.Validator))
            {
                var args = new Dictionary<string, object?>(config.DigitCollectionArguments ?? new Dictionary<string, object?>())
                {
                    ["__validator"] = collect.Validator,
                };
                config.DigitCollectionArguments = args;
            }
        }

        return config;
    }

    private static Dictionary<char, DtmfMenuOption> BuildMenu(IvrDtmfDocument doc, string stageId, List<string> errors)
    {
        var menu = new Dictionary<char, DtmfMenuOption>();
        foreach (var opt in doc.Options)
        {
            if (string.IsNullOrWhiteSpace(opt.Digit) || opt.Digit.Length != 1)
            {
                errors.Add($"Stage '{stageId}': DTMF option '{opt.Label}' has invalid digit '{opt.Digit}'.");
                continue;
            }
            var d = opt.Digit[0];
            if (menu.ContainsKey(d))
            {
                errors.Add($"Stage '{stageId}': DTMF digit '{d}' bound more than once.");
                continue;
            }

            // YAML keeps multiple routing fields; the runtime DtmfMenuOption supports
            // (ActionToolName, Arguments, NextStepId). Capabilities and intents are surfaced
            // through Arguments under reserved keys so the strategy can resolve them lazily.
            var args = opt.Args.Count > 0 ? new Dictionary<string, object?>(opt.Args) : null;
            if (opt.Capability is { Length: > 0 } cap)
            {
                args ??= [];
                args["__capability"] = cap;
            }
            if (opt.Intent is { Length: > 0 } intent)
            {
                args ??= [];
                args["__intent"] = intent;
            }

            menu[d] = new DtmfMenuOption
            {
                Digit = d,
                Label = opt.Label,
                ActionToolName = opt.Tool,
                Arguments = args,
                NextStepId = opt.NextStage,
                OnFailurePrompt = opt.OnFailurePrompt,
                OnFailureAudioFile = TryParseUri(opt.OnFailureAudioFile, stageId, $"option '{d}' onFailureAudioFile", errors),
            };
        }
        return menu;
    }

    private static char ParseDigit(string? value, char fallback, string stageId, string property, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        if (value.Length != 1)
        {
            errors.Add($"Stage '{stageId}' {property}: '{value}' must be a single character.");
            return fallback;
        }
        return value[0];
    }

    private static Uri? TryParseUri(string? value, string stageId, string property, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri;
        }
        errors.Add($"Stage '{stageId}' {property}: '{value}' is not an absolute URI.");
        return null;
    }
}
