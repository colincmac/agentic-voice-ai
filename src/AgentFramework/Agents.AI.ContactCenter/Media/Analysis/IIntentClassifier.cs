namespace Agents.AI.ContactCenter.Media.Analysis;

/// <summary>
/// Classifies user utterances into intents for deterministic IVR routing (Tier 3).
/// Implementations can wrap Azure Conversational Language Understanding (CLU),
/// regex-based matchers, keyword classifiers, or other NLU backends.
/// </summary>
public interface IIntentClassifier
{
    /// <summary>
    /// Classifies a user utterance against a set of valid intents for the current workflow step.
    /// </summary>
    /// <param name="utterance">The user's transcribed speech.</param>
    /// <param name="validIntents">
    /// The list of intent names that are valid for the current workflow step.
    /// Implementations should restrict classification to these intents.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The classification result, or a result with <see cref="IntentResult.IsNone"/> when no intent matches.</returns>
    ValueTask<IntentResult> ClassifyAsync(
        string utterance,
        IReadOnlyList<string> validIntents,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of an intent classification operation.
/// </summary>
public sealed class IntentResult
{
    /// <summary>
    /// The classified intent name, or null if no intent was matched.
    /// </summary>
    public string? IntentName { get; init; }

    /// <summary>
    /// Confidence score for the classification (0.0–1.0).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Extracted entities from the utterance, keyed by entity name.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Entities { get; init; }

    /// <summary>
    /// Returns true when no intent was matched.
    /// </summary>
    public bool IsNone => IntentName is null;

    /// <summary>
    /// A singleton representing no matched intent.
    /// </summary>
    public static IntentResult None { get; } = new() { Confidence = 0.0 };
}
