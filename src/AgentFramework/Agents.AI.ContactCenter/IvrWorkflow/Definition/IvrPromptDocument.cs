using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Definition;

/// <summary>
/// YAML projection of <see cref="Extensions.RealtimeAgentHelpers.Prompting.RealtimePrompt"/>.
/// Only the most commonly authored fields are exposed.
/// </summary>
public sealed class IvrPromptDocument
{
    /// <summary>Identity/objective (role) section.</summary>
    [YamlMember(Alias = "role")]
    public IvrRoleDocument? Role { get; set; }

    /// <summary>Personality/tone block.</summary>
    [YamlMember(Alias = "personality")]
    public IvrPersonalityDocument? Personality { get; set; }

    /// <summary>Free-form context appended to the rendered prompt.</summary>
    [YamlMember(Alias = "context")]
    public string? Context { get; set; }

    /// <summary>Reference pronunciations: words and the phonetic pronunciation hints.</summary>
    [YamlMember(Alias = "pronunciations")]
    public List<IvrPronunciationDocument> Pronunciations { get; set; } = [];

    /// <summary>Safety/escalation guidance.</summary>
    [YamlMember(Alias = "safety")]
    public IvrSafetyDocument? Safety { get; set; }
}

public sealed class IvrRoleDocument
{
    [YamlMember(Alias = "identity")]
    public string Identity { get; set; } = string.Empty;

    [YamlMember(Alias = "objective")]
    public string Objective { get; set; } = string.Empty;

    [YamlMember(Alias = "characterTraits")]
    public string? CharacterTraits { get; set; }
}

public sealed class IvrPersonalityDocument
{
    [YamlMember(Alias = "personality")]
    public string Personality { get; set; } = string.Empty;

    [YamlMember(Alias = "tone")]
    public string Tone { get; set; } = string.Empty;

    [YamlMember(Alias = "length")]
    public string? Length { get; set; }

    [YamlMember(Alias = "pacing")]
    public string? Pacing { get; set; }

    [YamlMember(Alias = "enthusiasm")]
    public string? Enthusiasm { get; set; }

    [YamlMember(Alias = "formality")]
    public string? Formality { get; set; }

    [YamlMember(Alias = "emotion")]
    public string? Emotion { get; set; }

    [YamlMember(Alias = "fillerWords")]
    public string? FillerWords { get; set; }

    [YamlMember(Alias = "enforceVariety")]
    public bool EnforceVariety { get; set; }
}

public sealed class IvrPronunciationDocument
{
    [YamlMember(Alias = "word")]
    public string Word { get; set; } = string.Empty;

    /// <summary>Phonetic guide (IPA or simplified). The YAML alias <c>ipa</c> is also accepted for ergonomics.</summary>
    [YamlMember(Alias = "pronunciation")]
    public string Pronunciation { get; set; } = string.Empty;

    /// <summary>Convenience alias for <see cref="Pronunciation"/>; populated when authors prefer <c>ipa: ...</c>.</summary>
    [YamlMember(Alias = "ipa")]
    public string? Ipa
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                Pronunciation = value;
            }
        }
    }
}

public sealed class IvrSafetyDocument
{
    [YamlMember(Alias = "escalateWhen")]
    public List<string> EscalateWhen { get; set; } = [];

    [YamlMember(Alias = "escalationPhrases")]
    public List<string> EscalationPhrases { get; set; } = [];

    [YamlMember(Alias = "maxFailedToolAttempts")]
    public int? MaxFailedToolAttempts { get; set; }

    [YamlMember(Alias = "maxNoMatchEvents")]
    public int? MaxNoMatchEvents { get; set; }

    [YamlMember(Alias = "escalationExamples")]
    public List<string> EscalationExamples { get; set; } = [];
}
