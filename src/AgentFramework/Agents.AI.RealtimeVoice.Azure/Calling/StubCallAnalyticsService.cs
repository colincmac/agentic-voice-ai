using System.Text.RegularExpressions;
using Agents.AI.RealtimeVoice.Azure.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

/// <summary>
/// Stub implementation of <see cref="ICallAnalyticsService"/> that uses simple keyword-based analysis.
/// </summary>
/// <remarks>
/// <para>
/// This is a placeholder implementation for development and testing purposes.
/// In production, replace with an implementation that calls real LLMs via Microsoft.Extensions.AI.
/// </para>
/// <para>
/// <b>TODO: For production use, implement real sentiment analysis using:</b>
/// </para>
/// <list type="bullet">
///   <item>Azure AI Language (Text Analytics) for sentiment</item>
///   <item>Azure OpenAI for advanced analysis</item>
///   <item>Microsoft.Extensions.AI for model abstraction</item>
/// </list>
/// </remarks>
public sealed partial class StubCallAnalyticsService : ICallAnalyticsService
{
    private readonly ILiveCallRegistry _liveCallRegistry;
    private readonly ILogger<StubCallAnalyticsService> _logger;

    // Sentiment keywords (simplified)
    private static readonly string[] positiveKeywords =
    [
        "thank", "thanks", "great", "good", "excellent", "wonderful", "amazing",
        "helpful", "appreciate", "perfect", "love", "happy", "pleased", "satisfied"
    ];

    private static readonly string[] negativeKeywords =
    [
        "angry", "frustrated", "upset", "terrible", "awful", "horrible", "hate",
        "annoyed", "disappointed", "problem", "issue", "complaint", "unacceptable",
        "ridiculous", "worst", "never", "cancel", "refund"
    ];

    private static readonly string[] escalationKeywords =
    [
        "manager", "supervisor", "escalate", "speak to someone", "higher up",
        "legal", "lawyer", "sue", "report", "complaint", "unacceptable",
        "never coming back", "cancel everything"
    ];

    public StubCallAnalyticsService(
        ILiveCallRegistry liveCallRegistry,
        ILogger<StubCallAnalyticsService>? logger = null)
    {
        _liveCallRegistry = liveCallRegistry;
        _logger = logger ?? NullLogger<StubCallAnalyticsService>.Instance;
    }

    /// <inheritdoc />
    public Task<CallHealthUpdate> AnalyzeUtteranceAsync(
        string sessionId,
        string speaker,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var lowerText = text.ToLowerInvariant();

        // Simple keyword-based sentiment analysis
        var sentiment = CalculateSentiment(lowerText);
        var escalationRisk = CalculateEscalationRisk(text, lowerText); // Pass both for caps detection
        var taskAdherence = CalculateTaskAdherence(lowerText);

        // Create summary (truncate long texts)
        var summary = text.Length > 100 ? text[..97] + "..." : text;

        var update = new CallHealthUpdate
        {
            SessionId = sessionId,
            Speaker = speaker,
            AnalyzedText = text,
            LatestUtteranceSummary = summary
        };

        // Update sentiment based on speaker
        bool isCustomer = IsCustomerSpeaker(speaker);
        if (isCustomer)
        {
            update.CustomerSentiment = sentiment;
            update.EscalationRiskScore = escalationRisk;
        }
        else
        {
            update.AgentSentiment = sentiment;
            update.TaskAdherenceScore = taskAdherence;
        }

        // Update the registry
        _liveCallRegistry.UpdateHealth(sessionId, call =>
        {
            if (isCustomer)
            {
                // Blend new sentiment with existing (if any) for smoother changes
                call.CustomerSentiment = BlendScore(call.CustomerSentiment, sentiment);
                call.EscalationRiskScore = BlendScore(call.EscalationRiskScore, escalationRisk);
            }
            else
            {
                call.AgentSentiment = BlendScore(call.AgentSentiment, sentiment);
                call.TaskAdherenceScore = BlendScore(call.TaskAdherenceScore, taskAdherence);
            }

            call.LatestUtteranceSummary = summary;
        });

        _logger.LogDebug(
            "Analyzed utterance for session {SessionId}: Speaker={Speaker}, Sentiment={Sentiment:F2}, EscalationRisk={Risk:F2}",
            sessionId,
            speaker,
            sentiment,
            escalationRisk);

        return Task.FromResult(update);
    }

    private static double CalculateSentiment(string lowerText)
    {
        int positiveCount = 0;
        int negativeCount = 0;

        foreach (var keyword in positiveKeywords)
        {
            if (lowerText.Contains(keyword, StringComparison.Ordinal))
            {
                positiveCount++;
            }
        }

        foreach (var keyword in negativeKeywords)
        {
            if (lowerText.Contains(keyword, StringComparison.Ordinal))
            {
                negativeCount++;
            }
        }

        if (positiveCount == 0 && negativeCount == 0)
        {
            return 0.0; // Neutral
        }

        // Calculate sentiment on -1 to 1 scale
        double total = positiveCount + negativeCount;
        return (positiveCount - negativeCount) / total;
    }

    private static double CalculateEscalationRisk(string originalText, string lowerText)
    {
        int escalationCount = 0;

        foreach (var keyword in escalationKeywords)
        {
            if (lowerText.Contains(keyword, StringComparison.Ordinal))
            {
                escalationCount++;
            }
        }

        // Also consider exclamation marks and ALL CAPS as escalation indicators
        int exclamationCount = originalText.Count(c => c == '!');
        int capsWordCount = CapsWordRegex().Matches(originalText).Count;

        // Normalize to 0-1 scale
        double rawScore = (escalationCount * 2 + exclamationCount + capsWordCount) / 10.0;
        return Math.Clamp(rawScore, 0.0, 1.0);
    }

    private static double CalculateTaskAdherence(string lowerText)
    {
        // Stub: Assume good adherence unless certain patterns are detected
        // In production, this would compare against expected script/procedures

        // Check for off-topic or unprofessional language
        bool hasUnprofessionalContent = lowerText.Contains("um", StringComparison.Ordinal) ||
                                         lowerText.Contains("uh", StringComparison.Ordinal);

        return hasUnprofessionalContent ? 0.7 : 0.9;
    }

    private static bool IsCustomerSpeaker(string speaker)
    {
        var lowerSpeaker = speaker.ToLowerInvariant();
        return lowerSpeaker is "user" or "customer" or "caller" or "human";
    }

    private static double BlendScore(double? existing, double newValue)
    {
        if (!existing.HasValue)
        {
            return newValue;
        }

        // Exponential moving average (70% new, 30% old)
        return (0.7 * newValue) + (0.3 * existing.Value);
    }

    [GeneratedRegex(@"\b[A-Z]{3,}\b")]
    private static partial Regex CapsWordRegex();
}
