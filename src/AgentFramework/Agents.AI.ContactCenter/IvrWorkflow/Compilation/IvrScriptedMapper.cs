using System;
using System.Collections.Generic;
using System.Linq;
using Agents.AI.ContactCenter.IvrWorkflow.Definition;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>
/// Lowers an <see cref="IvrScriptedStageDocument"/> into a runtime
/// <see cref="StepScriptedConfiguration"/> for the scripted (DTMF + NLU) tiers.
/// </summary>
/// <remarks>
/// Returns <see langword="null"/> when the input has no DTMF or NLU presence at all
/// (so generative-only stages produce <c>StepScriptedConfiguration = null</c>).
/// </remarks>
internal static class IvrScriptedMapper
{
    public static StepScriptedConfiguration? Map(
        IvrScriptedStageDocument doc,
        string stageId,
        List<string> errors,
        Func<IvrGuardDocument, IIvrStepGuard?>? guardBuilder = null)
    {
        if (doc.ConfidenceThreshold is < 0.0 or > 1.0)
        {
            errors.Add($"Stage '{stageId}' scripted.confidenceThreshold: '{doc.ConfidenceThreshold}' must be between 0.0 and 1.0.");
        }
        if (doc.MaxNoMatch < 0)
        {
            errors.Add($"Stage '{stageId}' scripted.maxNoMatch: must be \u2265 0.");
        }
        if (doc.MaxNoInput < 0)
        {
            errors.Add($"Stage '{stageId}' scripted.maxNoInput: must be \u2265 0.");
        }
        if (doc.NoInputTimeoutMs < 0)
        {
            errors.Add($"Stage '{stageId}' scripted.noInputTimeoutMs: must be \u2265 0.");
        }

        var nluCfg = doc.Nlu is { } nluDoc ? MapNlu(nluDoc, stageId, errors) : null;
        var dtmfCfg = doc.Dtmf is { } dtmfDoc ? IvrDtmfMapper.Map(dtmfDoc, stageId, errors, guardBuilder) : null;

        var hasSharedSignal =
            !string.IsNullOrEmpty(doc.SsmlPrompt)
            || !string.IsNullOrEmpty(doc.AudioFile)
            || !string.IsNullOrEmpty(doc.OnErrorPrompt)
            || !string.IsNullOrEmpty(doc.OnErrorAudioFile)
            || !string.IsNullOrEmpty(doc.OnNoInputPrompt)
            || !string.IsNullOrEmpty(doc.OnNoInputAudioFile)
            || !string.IsNullOrEmpty(doc.OnHandoffPrompt)
            || !string.IsNullOrEmpty(doc.OnHandoffAudioFile)
            || !string.IsNullOrEmpty(doc.OnConfirmPrompt)
            || !string.IsNullOrEmpty(doc.OnConfirmAudioFile)
            || doc.Examples.Count > 0;

        if (nluCfg is null && dtmfCfg is null && !hasSharedSignal)
        {
            return null;
        }

        return new StepScriptedConfiguration
        {
            SsmlPrompt = NullIfEmpty(doc.SsmlPrompt),
            AudioFile = TryParseUri(doc.AudioFile, stageId, "scripted.audioFile", errors),
            OnErrorPrompt = NullIfEmpty(doc.OnErrorPrompt),
            OnErrorAudioFile = TryParseUri(doc.OnErrorAudioFile, stageId, "scripted.onErrorAudioFile", errors),
            OnNoInputPrompt = NullIfEmpty(doc.OnNoInputPrompt),
            OnNoInputAudioFile = TryParseUri(doc.OnNoInputAudioFile, stageId, "scripted.onNoInputAudioFile", errors),
            OnHandoffPrompt = NullIfEmpty(doc.OnHandoffPrompt),
            OnHandoffAudioFile = TryParseUri(doc.OnHandoffAudioFile, stageId, "scripted.onHandoffAudioFile", errors),
            OnConfirmPrompt = NullIfEmpty(doc.OnConfirmPrompt),
            OnConfirmAudioFile = TryParseUri(doc.OnConfirmAudioFile, stageId, "scripted.onConfirmAudioFile", errors),
            MaxNoMatch = Math.Max(0, doc.MaxNoMatch),
            MaxNoInput = Math.Max(0, doc.MaxNoInput),
            NoInputTimeout = TimeSpan.FromMilliseconds(Math.Max(0, doc.NoInputTimeoutMs)),
            ConfidenceThreshold = Math.Clamp(doc.ConfidenceThreshold, 0.0, 1.0),
            Examples = doc.Examples.Count == 0 ? [] : doc.Examples.ToArray(),
            Nlu = nluCfg,
            Dtmf = dtmfCfg,
        };
    }

    private static StepNluConfiguration MapNlu(IvrNluDocument doc, string stageId, List<string> errors)
    {
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
            SsmlPromptOverride = NullIfEmpty(doc.SsmlPrompt),
            AudioFile = TryParseUri(doc.AudioFile, stageId, "scripted.nlu.audioFile", errors),
            IntentTransitions = transitions,
        };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

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
