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
        }

        return new IvrWorkflowValidationResult(errors);
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
