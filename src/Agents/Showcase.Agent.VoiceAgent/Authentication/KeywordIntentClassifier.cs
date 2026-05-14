using Agents.AI.ContactCenter.IvrWorkflow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Showcase.Agent.VoiceAgent.Authentication;

/// <summary>
/// Demo <see cref="IIntentClassifier"/> that matches utterances against keyword tables.
/// Keeps the showcase free of an Azure CLU dependency while still exercising the NLU
/// strategy's contract end-to-end. Returns <see cref="IntentResult.None"/> when no
/// keyword bucket matches and the utterance isn't a known transfer phrase.
/// </summary>
public sealed class KeywordIntentClassifier : IIntentClassifier
{
    private readonly ILogger<KeywordIntentClassifier> _logger;

    /// <summary>
    /// Static keyword → intent mapping. Workflow authors add the intent name to a step's
    /// <c>TransitionTo</c> so the NLU strategy will route on it.
    /// </summary>
    public IReadOnlyDictionary<string, string[]> IntentKeywords { get; init; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        // Transfer phrases — recognised even when the current step doesn't list them as a
        // valid transition; the NLU strategy adds "transfer_to_agent" automatically when an
        // escalation target is configured.
        ["transfer_to_agent"] = new[] { "agent", "human", "person", "representative", "operator", "real person", "transfer" },

        // Domain intents the showcase workflow uses.
        ["verify"] = new[] { "balance", "account", "billing", "billing question", "statement" },
        ["transfer"] = new[] { "agent", "supervisor", "manager" }
    };

    public KeywordIntentClassifier(ILogger<KeywordIntentClassifier>? logger = null)
    {
        _logger = logger ?? NullLogger<KeywordIntentClassifier>.Instance;
    }

    public ValueTask<IntentResult> ClassifyAsync(string utterance, IReadOnlyList<string> validIntents, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(utterance) || validIntents.Count == 0)
        {
            return ValueTask.FromResult(IntentResult.None);
        }

        var normalized = utterance.Trim().ToLowerInvariant();

        // Score every valid intent by keyword hit count; pick the highest-scoring one.
        string? bestIntent = null;
        var bestScore = 0;
        foreach (var intent in validIntents)
        {
            if (!IntentKeywords.TryGetValue(intent, out var keywords))
            {
                continue;
            }

            var score = keywords.Count(k => normalized.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (score > bestScore)
            {
                bestScore = score;
                bestIntent = intent;
            }
        }

        if (bestIntent is null)
        {
            _logger.LogDebug("No keyword match for utterance: {Utterance}", utterance);
            return ValueTask.FromResult(IntentResult.None);
        }

        var confidence = Math.Min(1.0, 0.4 + 0.2 * bestScore);
        _logger.LogInformation(
            "Classified '{Utterance}' as {Intent} (confidence {Confidence:F2})",
            utterance, bestIntent, confidence);

        return ValueTask.FromResult(new IntentResult
        {
            IntentName = bestIntent,
            Confidence = confidence
        });
    }
}
