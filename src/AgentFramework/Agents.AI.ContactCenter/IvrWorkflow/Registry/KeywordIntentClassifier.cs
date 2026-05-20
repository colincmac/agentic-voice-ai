using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agents.AI.ContactCenter.Media.Analysis;

namespace Agents.AI.ContactCenter.IvrWorkflow.Registry;

/// <summary>
/// Default <see cref="IIntentClassifier"/> that performs case-insensitive keyword
/// matching against intent <c>examples</c> declared in YAML. Used as a fallback when
/// no NLU service is registered; production hosts typically replace this with an Azure
/// CLU or LUIS-backed classifier.
/// </summary>
public sealed class KeywordIntentClassifier : IIntentClassifier
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _intentExamples;

    public KeywordIntentClassifier(IReadOnlyDictionary<string, IReadOnlyList<string>> intentExamples)
    {
        ArgumentNullException.ThrowIfNull(intentExamples);
        _intentExamples = intentExamples;
    }

    public ValueTask<IntentResult> ClassifyAsync(
        string utterance,
        IReadOnlyList<string> validIntents,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(utterance) || validIntents.Count == 0)
        {
            return ValueTask.FromResult(IntentResult.None);
        }

        var normalized = utterance.Trim();

        string? best = null;
        double bestScore = 0.0;
        foreach (var intent in validIntents)
        {
            if (!_intentExamples.TryGetValue(intent, out var examples))
            {
                continue;
            }

            var score = examples
                .Select(ex => ScoreMatch(normalized, ex))
                .DefaultIfEmpty(0.0)
                .Max();

            if (score > bestScore)
            {
                bestScore = score;
                best = intent;
            }
        }

        if (best is null || bestScore <= 0.0)
        {
            return ValueTask.FromResult(IntentResult.None);
        }

        return ValueTask.FromResult(new IntentResult
        {
            IntentName = best,
            Confidence = bestScore
        });
    }

    private static double ScoreMatch(string utterance, string example)
    {
        if (string.IsNullOrWhiteSpace(example))
        {
            return 0.0;
        }
        if (utterance.Equals(example, StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }
        if (utterance.Contains(example, StringComparison.OrdinalIgnoreCase))
        {
            return 0.85;
        }
        if (example.Contains(utterance, StringComparison.OrdinalIgnoreCase))
        {
            return 0.7;
        }
        return 0.0;
    }
}
