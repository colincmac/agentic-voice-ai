using Agents.AI.ContactCenter.IvrWorkflow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Showcase.Agent.IntentAgent.Classifiers;

/// <summary>
/// Demo <see cref="IIntentClassifier"/> backing the showcase gRPC intent
/// service. Matches utterances against keyword tables so the topology can be
/// exercised end-to-end without a real SLM. The production swap-in (Phi-4-mini
/// behind ONNX runtime / TorchSharp) plugs in behind the same interface and
/// requires no protocol change.
/// </summary>
public sealed class StubKeywordIntentClassifier : IIntentClassifier
{
    private readonly ILogger<StubKeywordIntentClassifier> _logger;

    public IReadOnlyDictionary<string, string[]> IntentKeywords { get; init; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["transfer_to_agent"] = new[] { "agent", "human", "person", "representative", "operator", "real person", "transfer" },
        ["verify"] = new[] { "balance", "account", "billing", "billing question", "statement" },
        ["transfer"] = new[] { "agent", "supervisor", "manager" }
    };

    public StubKeywordIntentClassifier(ILogger<StubKeywordIntentClassifier>? logger = null)
    {
        _logger = logger ?? NullLogger<StubKeywordIntentClassifier>.Instance;
    }

    public ValueTask<IntentResult> ClassifyAsync(string utterance, IReadOnlyList<string> validIntents, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(utterance) || validIntents.Count == 0)
        {
            return ValueTask.FromResult(IntentResult.None);
        }

        var normalized = utterance.Trim().ToLowerInvariant();

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
