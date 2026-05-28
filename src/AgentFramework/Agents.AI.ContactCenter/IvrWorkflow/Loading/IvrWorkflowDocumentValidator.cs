using System.Collections.Generic;
using System.Linq;
using Agents.AI.ContactCenter.IvrWorkflow.Definition;

namespace Agents.AI.ContactCenter.IvrWorkflow.Loading;

/// <summary>
/// Structural validation for an <see cref="IvrWorkflowDocument"/> after deserialization
/// and before compilation. Catches issues a JSON Schema cannot easily express
/// (cross-references, duplicate ids, transition targets, capability references).
/// </summary>
public static class IvrWorkflowDocumentValidator
{
    public static IvrWorkflowValidationResult Validate(IvrWorkflowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(document.Name))
        {
            errors.Add("Workflow 'name' is required.");
        }
        if (document.Stages.Count == 0)
        {
            errors.Add("Workflow must declare at least one stage.");
        }

        var stageIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < document.Stages.Count; i++)
        {
            var stage = document.Stages[i];
            var effectiveId = EffectiveStageId(stage);
            if (string.IsNullOrWhiteSpace(effectiveId))
            {
                errors.Add(stage.Import is not null
                    ? $"Import stage at index {i} is missing both 'id' and 'import.as'."
                    : $"Stage at index {i} is missing 'id'.");
                continue;
            }
            if (!stageIds.Add(effectiveId))
            {
                errors.Add($"Duplicate stage id '{effectiveId}'.");
            }
        }

        var capabilityIds = new HashSet<string>(document.Capabilities.Select(c => c.Id), StringComparer.Ordinal);
        foreach (var cap in document.Capabilities)
        {
            if (string.IsNullOrWhiteSpace(cap.Id))
            {
                errors.Add("Capability is missing 'id'.");
            }
        }

        foreach (var stage in document.Stages)
        {
            var effectiveId = EffectiveStageId(stage);
            if (string.IsNullOrWhiteSpace(effectiveId))
            {
                continue;
            }

            // Imported stages don't carry their own capability/transition declarations —
            // those came from the source stage and were validated when its workflow was
            // compiled. Skip the rest of the per-stage checks for imports.
            if (stage.Import is not null)
            {
                continue;
            }

            foreach (var capRef in stage.Capabilities)
            {
                if (!capabilityIds.Contains(capRef))
                {
                    errors.Add($"Stage '{effectiveId}' references unknown capability '{capRef}'.");
                }
            }

            foreach (var transition in stage.Transitions)
            {
                if (!string.IsNullOrWhiteSpace(transition.To) && !stageIds.Contains(transition.To))
                {
                    errors.Add($"Stage '{effectiveId}' transitions to unknown stage '{transition.To}'.");
                }
            }

            if (!string.IsNullOrWhiteSpace(stage.OnExit) && !stageIds.Contains(stage.OnExit))
            {
                errors.Add($"Stage '{effectiveId}' onExit references unknown stage '{stage.OnExit}'.");
            }

            ValidateIntents(effectiveId, stage.Intents, stageIds, capabilityIds, errors);

            if (stage.Scripted is { } scripted)
            {
                ValidateScripted(effectiveId, scripted, stageIds, capabilityIds, errors);
            }

            ValidateTransitionReachability(stage, errors);
        }

        return new IvrWorkflowValidationResult(errors);
    }

    /// <summary>
    /// Resolve the stage id the rest of the workflow refers to. Normal stages use
    /// <see cref="IvrStageDocument.Id"/>; Phase 2 import stages fall back to
    /// <see cref="IvrStageImportDocument.As"/>, then to the source stage id parsed out
    /// of <see cref="IvrStageImportDocument.Stage"/>.
    /// </summary>
    private static string EffectiveStageId(IvrStageDocument stage)
    {
        if (!string.IsNullOrWhiteSpace(stage.Id))
        {
            return stage.Id;
        }
        if (stage.Import is { } import)
        {
            if (!string.IsNullOrWhiteSpace(import.As))
            {
                return import.As!;
            }
            var reference = import.Stage ?? string.Empty;
            var lastDot = reference.LastIndexOf('.');
            if (lastDot > 0 && lastDot < reference.Length - 1)
            {
                return reference[(lastDot + 1)..];
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// Ensure non-terminal stages whose primary modality is realtime or NLU declare at
    /// least one way to leave the stage. Without this check a workflow can author a
    /// realtime stage with no transitions, no intents and no <c>on_exit</c>, and the
    /// realtime strategy will sit on the stage forever because there is no advance trigger
    /// the model can fire. DTMF-only stages are intentionally exempt because the DTMF
    /// runtime can still complete the call via <c>tool</c> / <c>capability</c> on a digit.
    /// </summary>
    private static void ValidateTransitionReachability(IvrStageDocument stage, List<string> errors)
    {
        if (stage.Terminal)
        {
            return;
        }

        if (HasTransitionAffordance(stage))
        {
            return;
        }

        // If the stage only has DTMF configured we let the DTMF-only path handle reachability —
        // the DTMF strategies can complete the call via menu options without an explicit transition.
        var hasNonDtmfModality = stage.Realtime is not null
            || (stage.Scripted?.Nlu is not null)
            || stage.Intents.Count > 0;
        if (!hasNonDtmfModality && stage.Scripted?.Dtmf is not null)
        {
            return;
        }

        errors.Add(
            $"Stage '{stage.Id}' is non-terminal but has no transitions, intents with nextStage, or onExit; " +
            "realtime/NLU strategies cannot advance from it. Add `transitions:`, an intent with `nextStage:`, or `onExit:`.");
    }

    private static bool HasTransitionAffordance(IvrStageDocument stage)
    {
        if (!string.IsNullOrWhiteSpace(stage.OnExit))
        {
            return true;
        }

        foreach (var t in stage.Transitions)
        {
            if (!string.IsNullOrWhiteSpace(t.To))
            {
                return true;
            }
        }

        foreach (var intent in stage.Intents)
        {
            if (!string.IsNullOrWhiteSpace(intent.NextStage)
                || !string.IsNullOrWhiteSpace(intent.Capability))
            {
                return true;
            }
        }

        if (stage.Scripted is { } scripted)
        {
            if (scripted.Nlu is { } nlu)
            {
                foreach (var intent in nlu.Intents)
                {
                    if (!string.IsNullOrWhiteSpace(intent.NextStage)
                        || !string.IsNullOrWhiteSpace(intent.Capability))
                    {
                        return true;
                    }
                }
            }

            if (scripted.Dtmf is { } dtmf)
            {
                foreach (var option in dtmf.Options)
                {
                    if (!string.IsNullOrWhiteSpace(option.NextStage)
                        || !string.IsNullOrWhiteSpace(option.Capability)
                        || !string.IsNullOrWhiteSpace(option.Tool))
                    {
                        return true;
                    }
                }

                if (dtmf.Collect is { } collect && !string.IsNullOrWhiteSpace(collect.OnValidNextStage))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void ValidateIntents(
        string stageId,
        IEnumerable<IvrIntentDocument> intents,
        ISet<string> stageIds,
        ISet<string> capabilityIds,
        List<string> errors)
    {
        foreach (var intent in intents)
        {
            if (string.IsNullOrWhiteSpace(intent.Name))
            {
                errors.Add($"Stage '{stageId}' has an intent with no name.");
                continue;
            }
            if (intent.NextStage is { Length: > 0 } next && !stageIds.Contains(next))
            {
                errors.Add($"Stage '{stageId}' intent '{intent.Name}' nextStage '{next}' is not defined.");
            }
            if (intent.Capability is { Length: > 0 } cap && !capabilityIds.Contains(cap))
            {
                errors.Add($"Stage '{stageId}' intent '{intent.Name}' references unknown capability '{cap}'.");
            }
        }
    }

    private static void ValidateScripted(
        string stageId,
        IvrScriptedStageDocument scripted,
        ISet<string> stageIds,
        ISet<string> capabilityIds,
        List<string> errors)
    {
        // Shared knobs.
        if (scripted.ConfidenceThreshold is < 0.0 or > 1.0)
        {
            errors.Add($"Stage '{stageId}' scripted.confidenceThreshold '{scripted.ConfidenceThreshold}' must be between 0.0 and 1.0.");
        }
        if (scripted.MaxNoMatch < 0)
        {
            errors.Add($"Stage '{stageId}' scripted.maxNoMatch must be \u2265 0.");
        }
        if (scripted.MaxNoInput < 0)
        {
            errors.Add($"Stage '{stageId}' scripted.maxNoInput must be \u2265 0.");
        }
        if (scripted.NoInputTimeoutMs < 0)
        {
            errors.Add($"Stage '{stageId}' scripted.noInputTimeoutMs must be \u2265 0.");
        }

        // Shared prompt/audio exclusivity per logical slot — when both are supplied the audio
        // wins at runtime but we warn at author time to surface likely mistakes.
        WarnIfBoth(stageId, "scripted.entry", scripted.SsmlPrompt, scripted.AudioFile, errors);
        WarnIfBoth(stageId, "scripted.error", scripted.OnErrorPrompt, scripted.OnErrorAudioFile, errors);
        WarnIfBoth(stageId, "scripted.noInput", scripted.OnNoInputPrompt, scripted.OnNoInputAudioFile, errors);
        WarnIfBoth(stageId, "scripted.confirm", scripted.OnConfirmPrompt, scripted.OnConfirmAudioFile, errors);
        WarnIfBoth(stageId, "scripted.handoff", scripted.OnHandoffPrompt, scripted.OnHandoffAudioFile, errors);

        if (scripted.Dtmf is { } dtmf)
        {
            ValidateDtmf(stageId, dtmf, stageIds, capabilityIds, errors);
            WarnIfBoth(stageId, "scripted.dtmf.entry", dtmf.SsmlPrompt, dtmf.AudioFile, errors);
        }

        if (scripted.Nlu is { } nlu)
        {
            WarnIfBoth(stageId, "scripted.nlu.entry", nlu.SsmlPrompt, nlu.AudioFile, errors);
            ValidateIntents(stageId, nlu.Intents, stageIds, capabilityIds, errors);
        }
    }

    private static void ValidateDtmf(
        string stageId,
        IvrDtmfDocument dtmf,
        ISet<string> stageIds,
        ISet<string> capabilityIds,
        List<string> errors)
    {
        var seenDigits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in dtmf.Options)
        {
            if (string.IsNullOrWhiteSpace(option.Digit) || option.Digit.Length != 1)
            {
                errors.Add($"Stage '{stageId}' DTMF option label '{option.Label}' has invalid digit '{option.Digit}'.");
                continue;
            }
            if (!seenDigits.Add(option.Digit))
            {
                errors.Add($"Stage '{stageId}' DTMF option digit '{option.Digit}' is bound more than once.");
            }
            if (option.NextStage is { Length: > 0 } next && !stageIds.Contains(next))
            {
                errors.Add($"Stage '{stageId}' DTMF digit '{option.Digit}' nextStage '{next}' is not defined.");
            }
            if (option.Capability is { Length: > 0 } cap && !capabilityIds.Contains(cap))
            {
                errors.Add($"Stage '{stageId}' DTMF digit '{option.Digit}' references unknown capability '{cap}'.");
            }
        }

        if (dtmf.Collect is { } collect && collect.OnValidNextStage is { Length: > 0 } onValid
            && !stageIds.Contains(onValid))
        {
            errors.Add($"Stage '{stageId}' DTMF collect.onValidNextStage '{onValid}' is not defined.");
        }
    }

    private static void WarnIfBoth(string stageId, string slot, string? text, string? audio, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(audio))
        {
            errors.Add($"Stage '{stageId}' {slot}: both prompt text and audioFile are set; supply only one (audioFile would win at runtime).");
        }
    }
}

/// <summary>Aggregated result of <see cref="IvrWorkflowDocumentValidator.Validate"/>.</summary>
public sealed class IvrWorkflowValidationResult
{
    public IvrWorkflowValidationResult(IReadOnlyList<string> errors)
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
    public bool IsValid => Errors.Count == 0;

    public void ThrowIfInvalid(string? context = null)
    {
        if (IsValid)
        {
            return;
        }
        var prefix = context is null
            ? "IVR workflow document is invalid:"
            : $"IVR workflow '{context}' is invalid:";
        throw new IvrWorkflowYamlException(prefix + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", Errors));
    }
}
