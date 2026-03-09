namespace Agents.AI.Extensions.LiveVoice.Media.Analysis;

/// <summary>
/// Pluggable pipeline that extracts paralingual features from raw audio.
/// <para>
/// Implementations can be:
/// <list type="bullet">
///   <item>A local ONNX model for lightweight inference</item>
///   <item>An Azure AI Speech / Azure AI Language API call</item>
///   <item>An A2A agent that accepts audio and returns structured analysis</item>
/// </list>
/// The transport batches audio frames and calls this on a cadence,
/// not per-frame — implementations can assume multi-second windows.
/// </para>
/// </summary>
public interface IAudioAnalysisPipeline
{
    /// <summary>
    /// Analyzes a buffered window of PCM audio and returns paralingual features.
    /// </summary>
    /// <param name="audioWindow">Buffered PCM audio for the analysis window.</param>
    /// <param name="sampleRateHz">Sample rate of the audio data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Analysis result, or null if the window had insufficient audio.</returns>
    Task<AudioAnalysisResult?> AnalyzeAsync(
        ReadOnlyMemory<byte> audioWindow,
        int sampleRateHz = 16_000,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result from a single analysis window.
/// </summary>
public sealed record AudioAnalysisResult
{
    public EmotionSignal? Emotion { get; init; }
    public double? SpeechRate { get; init; }
    public double? StressLevel { get; init; }
    public double? OverallConfidence { get; init; }
}
