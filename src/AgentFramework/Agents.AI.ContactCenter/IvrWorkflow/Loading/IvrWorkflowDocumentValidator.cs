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
            if (string.IsNullOrWhiteSpace(stage.Id))
            {
                errors.Add($"Stage at index {i} is missing 'id'.");
                continue;
            }
            if (!stageIds.Add(stage.Id))
            {
                errors.Add($"Duplicate stage id '{stage.Id}'.");
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
            if (string.IsNullOrWhiteSpace(stage.Id))
            {
                continue;
            }

            foreach (var capRef in stage.Capabilities)
            {
                if (!capabilityIds.Contains(capRef))
                {
                    errors.Add($"Stage '{stage.Id}' references unknown capability '{capRef}'.");
                }
            }

            foreach (var transition in stage.Transitions)
            {
                if (!string.IsNullOrWhiteSpace(transition.To) && !stageIds.Contains(transition.To))
                {
                    errors.Add($"Stage '{stage.Id}' transitions to unknown stage '{transition.To}'.");
                }
            }

            if (!string.IsNullOrWhiteSpace(stage.OnExit) && !stageIds.Contains(stage.OnExit))
            {
                errors.Add($"Stage '{stage.Id}' onExit references unknown stage '{stage.OnExit}'.");
            }

            ValidateIntents(stage.Id, stage.Intents, stageIds, capabilityIds, errors);

            if (stage.Dtmf is { } dtmf)
            {
                ValidateDtmf(stage.Id, dtmf, stageIds, capabilityIds, errors);
            }

            if (stage.Nlu is { } nlu)
            {
                ValidateNlu(stage.Id, nlu, stageIds, capabilityIds, errors);
            }

            ValidateTransitionReachability(stage, errors);
        }

        return new IvrWorkflowValidationResult(errors);
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
        var hasNonDtmfModality = stage.Realtime is not null || stage.Nlu is not null || stage.Intents.Count > 0;
        if (!hasNonDtmfModality && stage.Dtmf is not null)
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

        if (stage.Nlu is { } nlu)
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

        if (stage.Dtmf is { } dtmf)
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

    private static void ValidateNlu(
        string stageId,
        IvrNluDocument nlu,
        ISet<string> stageIds,
        ISet<string> capabilityIds,
        List<string> errors)
    {
        if (nlu.ConfidenceThreshold is < 0.0 or > 1.0)
        {
            errors.Add($"Stage '{stageId}' nlu.confidenceThreshold '{nlu.ConfidenceThreshold}' must be between 0.0 and 1.0.");
        }
        if (nlu.MaxNoMatch < 0)
        {
            errors.Add($"Stage '{stageId}' nlu.maxNoMatch must be \u2265 0.");
        }
        if (nlu.MaxNoInput < 0)
        {
            errors.Add($"Stage '{stageId}' nlu.maxNoInput must be \u2265 0.");
        }
        if (nlu.NoInputTimeoutMs < 0)
        {
            errors.Add($"Stage '{stageId}' nlu.noInputTimeoutMs must be \u2265 0.");
        }

        // Prompt/audio exclusivity per logical slot — when both are supplied the audio
        // wins at runtime but we warn at author time to surface likely mistakes.
        WarnIfBoth(stageId, "entry", nlu.SsmlPrompt, nlu.AudioFile, errors);
        WarnIfBoth(stageId, "noMatch", nlu.OnNoMatchPrompt, nlu.OnNoMatchAudioFile, errors);
        WarnIfBoth(stageId, "noInput", nlu.OnNoInputPrompt, nlu.OnNoInputAudioFile, errors);
        WarnIfBoth(stageId, "confirm", nlu.OnConfirmPrompt, nlu.OnConfirmAudioFile, errors);
        WarnIfBoth(stageId, "handoff", nlu.OnHandoffPrompt, nlu.OnHandoffAudioFile, errors);

        ValidateIntents(stageId, nlu.Intents, stageIds, capabilityIds, errors);
    }

    private static void WarnIfBoth(string stageId, string slot, string? text, string? audio, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(audio))
        {
            errors.Add($"Stage '{stageId}' nlu.{slot}: both prompt text and audioFile are set; supply only one (audioFile would win at runtime).");
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
