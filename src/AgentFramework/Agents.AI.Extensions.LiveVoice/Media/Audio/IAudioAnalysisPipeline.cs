namespace Agents.AI.Extensions.LiveVoice.Media.Audio;

/// <summary>
/// Runs lightweight paralingual analysis on raw audio frames,
/// producing structured signals (emotion, speech rate, stress)
/// without requiring transcription.
/// </summary>
public interface IAudioAnalysisPipeline
{
    /// <summary>
    /// Analyzes a chunk of PCM audio and returns any detected signals.
    /// Implementations should be fast and non-blocking — this runs
    /// on the hot path alongside the realtime AI stream.
    /// </summary>
    Task<AudioAnalysisResult> AnalyzeAsync(
        ReadOnlyMemory<byte> pcmAudio,
        AudioAnalysisContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Aggregated result from one or more paralingual analyzers.
/// </summary>
public sealed class AudioAnalysisResult
{
    public EmotionSignal? Emotion { get; init; }
    public double? SpeechRate { get; init; }
    public double? StressLevel { get; init; }
    public double? Confidence { get; init; }
}

public sealed class EmotionSignal
{
    public required string Label { get; init; }
    public required double Score { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Session-level context passed to analyzers so they can adapt
/// to the conversation's audio distribution (telephony vs. high-quality, etc.).
/// </summary>
public sealed class AudioAnalysisContext
{
    public string? SessionId { get; init; }
    public AudioEnvironmentProfile? EnvironmentProfile { get; init; }
    public IReadOnlyDictionary<string, object?>? ConversationState { get; init; }
}

public sealed class AudioEnvironmentProfile
{
    public required AudioQualityTier Quality { get; init; }
    public double? EstimatedSnr { get; init; }
    public int SampleRateHz { get; init; } = 16_000;
}

public enum AudioQualityTier
{
    Telephony8kHz,
    StandardVoIP,
    HighQuality,
    Unknown
}
