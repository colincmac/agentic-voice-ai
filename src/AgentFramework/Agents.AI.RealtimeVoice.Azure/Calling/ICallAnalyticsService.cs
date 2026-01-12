using Agents.AI.RealtimeVoice.Azure.Calling.Models;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

/// <summary>
/// DTO containing the results of call health analysis.
/// </summary>
public sealed class CallHealthUpdate
{
    /// <summary>
    /// The session ID that was analyzed.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Updated customer sentiment score (-1 to 1).
    /// </summary>
    public double? CustomerSentiment { get; set; }

    /// <summary>
    /// Updated agent sentiment score (-1 to 1).
    /// </summary>
    public double? AgentSentiment { get; set; }

    /// <summary>
    /// Updated task adherence score (0 to 1).
    /// </summary>
    public double? TaskAdherenceScore { get; set; }

    /// <summary>
    /// Updated escalation risk score (0 to 1).
    /// </summary>
    public double? EscalationRiskScore { get; set; }

    /// <summary>
    /// Updated list of active tasks or intents.
    /// </summary>
    public IReadOnlyList<string>? ActiveTasks { get; set; }

    /// <summary>
    /// Summary of the latest utterance analyzed.
    /// </summary>
    public string? LatestUtteranceSummary { get; set; }

    /// <summary>
    /// The speaker role (e.g., "user", "assistant", "customer", "agent").
    /// </summary>
    public string? Speaker { get; set; }

    /// <summary>
    /// The original text that was analyzed.
    /// </summary>
    public string? AnalyzedText { get; set; }
}

/// <summary>
/// Service for analyzing call health metrics from utterances.
/// </summary>
public interface ICallAnalyticsService
{
    /// <summary>
    /// Analyzes the latest utterance and updates call health metrics.
    /// </summary>
    /// <param name="sessionId">The session ID of the call.</param>
    /// <param name="speaker">The speaker role (e.g., "user", "assistant").</param>
    /// <param name="text">The text of the utterance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated health metrics for the call.</returns>
    Task<CallHealthUpdate> AnalyzeUtteranceAsync(
        string sessionId,
        string speaker,
        string text,
        CancellationToken cancellationToken = default);
}
