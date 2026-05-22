using System;
using System.Collections.Generic;
using Agents.AI.ContactCenter.IvrWorkflow.Definition;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>
/// Lowers <see cref="IvrNluDocument"/> into the runtime
/// <see cref="StepNluConfiguration"/> shape consumed by <c>NluConversationStrategy</c>.
/// Parallel in spirit to <see cref="IvrDtmfMapper"/>.
/// </summary>
internal static class IvrNluMapper
{
    public static StepNluConfiguration Map(IvrNluDocument doc, string stageId, List<string> errors)
    {
        if (doc.ConfidenceThreshold is < 0.0 or > 1.0)
        {
            errors.Add($"Stage '{stageId}' nlu.confidenceThreshold: '{doc.ConfidenceThreshold}' must be between 0.0 and 1.0.");
        }
        if (doc.MaxNoMatch < 0)
        {
            errors.Add($"Stage '{stageId}' nlu.maxNoMatch: must be \u2265 0.");
        }
        if (doc.MaxNoInput < 0)
        {
            errors.Add($"Stage '{stageId}' nlu.maxNoInput: must be \u2265 0.");
        }
        if (doc.NoInputTimeoutMs < 0)
        {
            errors.Add($"Stage '{stageId}' nlu.noInputTimeoutMs: must be \u2265 0.");
        }

        var transitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var intent in doc.Intents)
        {
            if (string.IsNullOrWhiteSpace(intent.Name) || intent.NextStage is not { Length: > 0 } next)
            {
                continue;
            }
            transitions[intent.Name] = next;
        }

        return new StepNluConfiguration
        {
            SsmlPromptOverride = doc.SsmlPrompt,
            AudioFile = TryParseUri(doc.AudioFile, stageId, "nlu.audioFile", errors),
            OnNoMatchPrompt = doc.OnNoMatchPrompt,
            OnNoMatchAudioFile = TryParseUri(doc.OnNoMatchAudioFile, stageId, "nlu.onNoMatchAudioFile", errors),
            OnNoInputPrompt = doc.OnNoInputPrompt,
            OnNoInputAudioFile = TryParseUri(doc.OnNoInputAudioFile, stageId, "nlu.onNoInputAudioFile", errors),
            OnConfirmPrompt = doc.OnConfirmPrompt,
            OnConfirmAudioFile = TryParseUri(doc.OnConfirmAudioFile, stageId, "nlu.onConfirmAudioFile", errors),
            OnHandoffPrompt = doc.OnHandoffPrompt,
            OnHandoffAudioFile = TryParseUri(doc.OnHandoffAudioFile, stageId, "nlu.onHandoffAudioFile", errors),
            MaxNoMatch = Math.Max(0, doc.MaxNoMatch),
            MaxNoInput = Math.Max(0, doc.MaxNoInput),
            ConfidenceThreshold = Math.Clamp(doc.ConfidenceThreshold, 0.0, 1.0),
            NoInputTimeout = TimeSpan.FromMilliseconds(Math.Max(0, doc.NoInputTimeoutMs)),
            Examples = doc.Examples.Count == 0 ? [] : doc.Examples.ToArray(),
            IntentTransitions = transitions,
        };
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
