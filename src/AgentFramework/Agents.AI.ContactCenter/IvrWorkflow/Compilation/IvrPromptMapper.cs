using System.Collections.Generic;
using System.Linq;
using Agents.AI.ContactCenter.IvrWorkflow.Definition;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>Maps YAML prompt POCOs to the runtime <see cref="RealtimePrompt"/> record graph.</summary>
internal static class IvrPromptMapper
{
    public static RealtimePrompt MapBasePrompt(IvrPromptDocument? prompt) => new()
    {
        Role = MapRole(prompt?.Role),
        Personality = MapPersonality(prompt?.Personality),
        Context = string.IsNullOrWhiteSpace(prompt?.Context) ? null : prompt!.Context,
        ReferencePronunciations = MapPronunciations(prompt?.Pronunciations),
        Safety = MapSafety(prompt?.Safety),
    };

    public static IReadOnlyList<ToolUsageRule>? MapToolRules(IEnumerable<IvrToolRuleDocument>? rules)
    {
        if (rules is null)
        {
            return null;
        }
        var list = rules
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.UseWhen))
            .Select(MapRule)
            .ToList();
        return list.Count == 0 ? null : list;
    }

    private static ToolUsageRule MapRule(IvrToolRuleDocument r) => new()
    {
        Name = r.Name,
        UseWhen = r.UseWhen,
        DoNotUseWhen = r.DoNotUseWhen,
        Behavior = ParseBehavior(r.Behavior),
        PreamblePhrases = r.PreamblePhrases.Count == 0 ? null : r.PreamblePhrases,
        ConfirmationPhrase = r.ConfirmationPhrase,
    };

    private static ToolBehavior ParseBehavior(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "confirmationfirst" => ToolBehavior.ConfirmationFirst,
        "preamblefirst" or "preambles" => ToolBehavior.Preambles,
        _ => ToolBehavior.Proactive,
    };

    private static RoleAndObjective? MapRole(IvrRoleDocument? role)
    {
        if (role is null || string.IsNullOrWhiteSpace(role.Identity) || string.IsNullOrWhiteSpace(role.Objective))
        {
            return null;
        }
        return new RoleAndObjective
        {
            Identity = role.Identity,
            Objective = role.Objective,
            CharacterTraits = role.CharacterTraits,
        };
    }

    private static PersonalityAndTone? MapPersonality(IvrPersonalityDocument? p)
    {
        if (p is null || string.IsNullOrWhiteSpace(p.Personality) || string.IsNullOrWhiteSpace(p.Tone))
        {
            return null;
        }
        return new PersonalityAndTone
        {
            Personality = p.Personality,
            Tone = p.Tone,
            Length = p.Length,
            Pacing = p.Pacing,
            Enthusiasm = p.Enthusiasm,
            Formality = p.Formality,
            Emotion = p.Emotion,
            FillerWords = p.FillerWords,
            EnforceVariety = p.EnforceVariety,
        };
    }

    private static IReadOnlyList<ReferencePronunciation>? MapPronunciations(IEnumerable<IvrPronunciationDocument>? list)
    {
        if (list is null)
        {
            return null;
        }
        var mapped = list
            .Where(p => !string.IsNullOrWhiteSpace(p.Word) && !string.IsNullOrWhiteSpace(p.Pronunciation))
            .Select(p => new ReferencePronunciation
            {
                Word = p.Word,
                Pronunciation = p.Pronunciation,
            })
            .ToList();
        return mapped.Count == 0 ? null : mapped;
    }

    private static SafetyAndEscalation? MapSafety(IvrSafetyDocument? safety)
    {
        if (safety is null)
        {
            return null;
        }
        if (safety.EscalateWhen.Count == 0
            && safety.EscalationPhrases.Count == 0
            && safety.EscalationExamples.Count == 0
            && !safety.MaxFailedToolAttempts.HasValue
            && !safety.MaxNoMatchEvents.HasValue)
        {
            return null;
        }
        return new SafetyAndEscalation
        {
            EscalateWhen = safety.EscalateWhen.Count == 0 ? [] : safety.EscalateWhen,
            EscalationPhrases = safety.EscalationPhrases.Count == 0 ? null : safety.EscalationPhrases,
            EscalationExamples = safety.EscalationExamples.Count == 0 ? null : safety.EscalationExamples,
            MaxFailedToolAttempts = safety.MaxFailedToolAttempts,
            MaxNoMatchEvents = safety.MaxNoMatchEvents,
        };
    }
}
