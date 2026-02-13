using Agents.AI.Extensions.Helpers.Streaming;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Transports;

/// <summary>
/// Processes a specific data modality (screen share, document, video)
/// and publishes structured insights to the <see cref="SessionContextBus"/>.
/// Runs as a background participant in the session.
/// <para>
/// A modality processor subscribes to a <see cref="RawMediaStreamChannel"/>
/// (e.g., a screen share stream) via a <see cref="RawMediaPipeSubscription"/>,
/// processes the frames through a specialized AI model, and publishes
/// <see cref="ContextEventKind.ModalityInsight"/> events that the primary
/// voice AI agent can consume as additional context.
/// </para>
/// </summary>
/// <example>
/// <code>
/// // Register a screen analysis processor:
/// await session.AddModalityProcessorAsync(
///     "screen-analyzer",
///     new ScreenAnalysisProcessor(visionModel),
///     screenShareChannel);
/// </code>
/// </example>
public interface IModalityProcessor : IAsyncDisposable
{
    /// <summary>
    /// Human-readable name for this processor (e.g., "Screen Analysis Agent").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The data modality this processor handles (e.g., "screen", "document", "video").
    /// </summary>
    string Modality { get; }

    /// <summary>
    /// Whether the processor is currently running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Starts processing. The processor reads frames from the <paramref name="dataSubscription"/>
    /// and publishes <see cref="ContextEventKind.ModalityInsight"/> events to the <paramref name="contextBus"/>.
    /// </summary>
    /// <param name="dataSubscription">
    /// A subscription to a <see cref="RawMediaStreamChannel"/> producing the data stream to analyze.
    /// </param>
    /// <param name="contextBus">
    /// The session's context bus where insights are published.
    /// </param>
    /// <param name="sessionId">The session identifier for telemetry and correlation.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    Task StartAsync(
        RawMediaPipeSubscription dataSubscription,
        SessionContextBus contextBus,
        string sessionId,
        CancellationToken cancellationToken = default);
}
