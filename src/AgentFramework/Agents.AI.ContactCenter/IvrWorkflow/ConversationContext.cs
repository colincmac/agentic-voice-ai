using System.Collections.Concurrent;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Media.Analysis;

namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// Pre-registered structured memory for a live conversation session.
/// Always available to the agent — eliminates unpredictable memory retrieval
/// failures. Inspired by the "pinned header" pattern: this data is always
/// front-and-center regardless of conversation length.
/// </summary>
public sealed class ConversationContext
{
    // Participant identity
    public string? CallerName { get; set; }
    public string? CallerId { get; set; }
    public CallerVerificationLevel VerificationLevel { get; set; } = CallerVerificationLevel.None;

    // Intent tracking
    public string? PrimaryIntent { get; set; }
    public List<string> SecondaryIntents { get; } = [];
    public bool IntentConfirmed { get; set; }

    // Emotional trajectory (structured, not ad-hoc)
    public double RunningTextSentiment { get; set; }
    public double RunningAudioEmotion { get; set; }
    public bool FrustrationDetected { get; set; }
    public int EscalationSignalCount { get; set; }

    // Conversation shape
    public int TurnCount { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public double AvgUserUtteranceLengthSec { get; set; }

    // Audio environment (classified once at session start, updated if it changes)
    public AudioQualityTier AudioQuality { get; set; } = AudioQualityTier.Unknown;
    public double? EstimatedSignalToNoiseRatio { get; set; }

    // Summary (updated at step transitions, not every turn)
    public string? ConversationSummary { get; set; }
    public List<string> ActionsTaken { get; } = [];

    public ConcurrentDictionary<string, object> AdditionalProperties { get; } = new();
}
