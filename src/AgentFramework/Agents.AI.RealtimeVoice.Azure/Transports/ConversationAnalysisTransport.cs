using System.Buffers;
using System.Diagnostics;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.Extensions.LiveVoice.Media.Analysis;
using Agents.AI.Extensions.LiveVoice.Media.Audio;
using Agents.AI.Extensions.LiveVoice.Media.Messaging;
using Agents.AI.RealtimeVoice.Azure.Calling;
using Agents.AI.RealtimeVoice.Azure.Monitoring;
using Agents.AI.RealtimeVoice.Azure.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Transports;

/// <summary>
/// Silent observer transport that performs cross-signal analysis on a conversation
/// by consuming audio frames and subscribing to transcript events on the
/// <see cref="HubSessionEventBus"/>.
/// <para>
/// Implements <see cref="IAudioConsumer"/> so the session router delivers the caller's
/// audio frames to it (just like it delivers to the voice AI agent). Does not produce
/// audio — it only publishes <see cref="ConversationSignalAnalysis"/> results back
/// to the event bus as <see cref="HubSessionEventKind.AgentInsight"/> events.
/// </para>
/// <para>
/// Design follows the podcast's ensemble principle: a small, specialized analysis
/// pipeline runs in parallel to the large realtime model, with cross-signal
/// validation detecting when text and voice disagree.
/// </para>
/// </summary>
public sealed class ConversationAnalysisTransport : IChannelTransport, IAudioConsumer, IMessageProducer
{
    private readonly IAudioAnalysisPipeline _audioPipeline;
    private readonly ITextSentimentAnalyzer _textAnalyzer;
    private readonly CrossSignalCorrelator _correlator;
    private readonly HubSessionEventBus _eventBus;
    private readonly SessionTelemetry? _telemetry;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _channelId;
    private readonly string? _sessionId;

    // Audio buffering — accumulate frames into analysis windows
    private readonly MemoryStream _audioBuffer = new();
    private readonly Lock _bufferLock = new();
    private readonly int _analysisWindowMs;
    private readonly int _sampleRateHz;
    private DateTimeOffset _windowStart = DateTimeOffset.UtcNow;

    // Background loops
    private Task? _analysisLoop;
    private Task? _contextLoop;
    private SessionContextSubscription? _contextSubscription;

    // Outbound message handler (for emitting results as MessageUpdate if needed)
    private Func<string, MessageUpdate, CancellationToken, Task>? _messageHandler;
    private Func<string, Task>? _disconnectedHandler;

    /// <summary>
    /// Creates a new analysis transport.
    /// </summary>
    /// <param name="audioPipeline">The audio analysis implementation (ONNX, Azure AI, A2A agent, etc.).</param>
    /// <param name="textAnalyzer">The text sentiment implementation (Azure AI Language, A2A agent, etc.).</param>
    /// <param name="eventBus">The session's event bus for subscribing to transcripts and publishing results.</param>
    /// <param name="channelId">Optional channel identifier. Defaults to a generated ID.</param>
    /// <param name="analysisWindowMs">How often to run audio analysis, in milliseconds.</param>
    /// <param name="sampleRateHz">Expected audio sample rate.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    public ConversationAnalysisTransport(
        IAudioAnalysisPipeline audioPipeline,
        ITextSentimentAnalyzer textAnalyzer,
        HubSessionEventBus eventBus,
        string? channelId = null,
        int analysisWindowMs = 3_000,
        int sampleRateHz = 16_000,
        string? sessionId = null,
        SessionTelemetry? telemetry = null,
        ILoggerFactory? loggerFactory = null)
    {
        _audioPipeline = audioPipeline;
        _textAnalyzer = textAnalyzer;
        _eventBus = eventBus;
        _channelId = channelId ?? $"analysis-{Guid.NewGuid():N}";
        _analysisWindowMs = analysisWindowMs;
        _sampleRateHz = sampleRateHz;
        _sessionId = sessionId;
        _telemetry = telemetry;
        _correlator = new CrossSignalCorrelator();
        _logger = loggerFactory?.CreateLogger<ConversationAnalysisTransport>()
                  ?? NullLogger<ConversationAnalysisTransport>.Instance;

        Metadata = new ParticipantTransportMetadata
        {
            ContactId = _channelId,
            ChannelType = CommunicationChannelType.A2AAgent,
            RawIdentifier = _channelId,
            DisplayName = "Cross-Signal Analyzer",
            SupportsAudio = true,
            SupportsMessaging = true,
            Role = ChannelRole.AgentToAgent | ChannelRole.ContextSink
        };
    }

    public string ChannelId => _channelId;
    public ParticipantTransportMetadata Metadata { get; }
    public bool IsConnected => _analysisLoop is not null;

    /// <summary>
    /// Exposes the correlator for direct queries (e.g., escalation checks).
    /// </summary>
    public CrossSignalCorrelator Correlator => _correlator;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_analysisLoop is not null)
        {
            return Task.CompletedTask;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);

        // Subscribe to transcripts on the event bus for text sentiment
        _contextSubscription = _eventBus.Subscribe(e =>
            e.SourceParticipantId != _channelId &&
            e.Kind is HubSessionEventKind.Transcript or HubSessionEventKind.ChatMessage);
        _contextLoop = ProcessTranscriptEventsAsync(_contextSubscription, linked.Token);

        // Periodic audio analysis loop
        _analysisLoop = RunAudioAnalysisLoopAsync(linked.Token);

        _logger.LogInformation(
            "ConversationAnalysisTransport {ChannelId} connected (window={WindowMs}ms)",
            _channelId, _analysisWindowMs);

        return Task.CompletedTask;
    }

    public void SetOnMessageReceivedCallback(Func<string, MessageUpdate, CancellationToken, Task> handler)
        => _messageHandler = handler;

    public void SetOnDisconnected(Func<string, Task> handler)
        => _disconnectedHandler = handler;

    /// <summary>
    /// Receives audio frames from the session router. Frames are buffered
    /// and analyzed in batches by the background analysis loop.
    /// </summary>
    public Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        lock (_bufferLock)
        {
            _audioBuffer.Write(audioData.Span);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Background loop: drains the audio buffer at the configured cadence,
    /// runs the audio analysis pipeline, cross-correlates with text sentiment,
    /// and publishes results to the event bus.
    /// </summary>
    private async Task RunAudioAnalysisLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_analysisWindowMs));

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                ReadOnlyMemory<byte> window;
                TimeSpan windowDuration;

                lock (_bufferLock)
                {
                    if (_audioBuffer.Length == 0)
                    {
                        continue;
                    }

                    window = _audioBuffer.ToArray();
                    windowDuration = DateTimeOffset.UtcNow - _windowStart;
                    _audioBuffer.SetLength(0);
                    _windowStart = DateTimeOffset.UtcNow;
                }

                try
                {
                    var analysisStart = Stopwatch.GetTimestamp();
                    var audioResult = await _audioPipeline
                        .AnalyzeAsync(window, _sampleRateHz, ct)
                        .ConfigureAwait(false);

                    if (audioResult?.Emotion is not null)
                    {
                        _correlator.RecordAudioEmotion(audioResult.Emotion);
                    }

                    // Cross-correlate and publish
                    var analysis = _correlator.Evaluate(audioResult);
                    if (analysis is not null)
                    {
                        if (_telemetry is not null && !string.IsNullOrWhiteSpace(_sessionId))
                        {
                            _telemetry.RecordSignalAnalysis(
                                _sessionId,
                                analysis,
                                Stopwatch.GetElapsedTime(analysisStart).TotalMilliseconds);
                        }

                        await PublishAnalysisAsync(analysis, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Audio analysis failed for window at {WindowStart}",
                        _windowStart);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    /// <summary>
    /// Processes transcript events from the event bus, runs text sentiment
    /// analysis, and feeds results into the cross-signal correlator.
    /// </summary>
    private async Task ProcessTranscriptEventsAsync(
        SessionContextSubscription subscription, CancellationToken ct)
    {
        try
        {
            await foreach (var contextEvent in subscription.ReadAllAsync(ct).ConfigureAwait(false))
            {
                string? text = contextEvent.Payload switch
                {
                    MessageUpdate mu => mu.Contents.OfType<TextContent>().FirstOrDefault()?.Text,
                    string s => s,
                    _ => null
                };

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                try
                {
                    var sentiment = await _textAnalyzer
                        .AnalyzeSentimentAsync(text, ct)
                        .ConfigureAwait(false);

                    if (sentiment.HasValue)
                    {
                        _correlator.RecordTextSentiment(sentiment.Value);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Text sentiment analysis failed for transcript event {EventId}",
                        contextEvent.EventId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    private async Task PublishAnalysisAsync(ConversationSignalAnalysis analysis, CancellationToken ct)
    {
        // Publish to event bus — the primary voice AI agent and dashboard can subscribe
        await _eventBus.PublishAsync(new SessionContextEvent
        {
            EventId = $"signal_analysis_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            Kind = HubSessionEventKind.AgentInsight,
            SourceParticipantId = _channelId,
            Payload = analysis,
            Tags = new Dictionary<string, string>
            {
                ["insight.type"] = "cross_signal_analysis",
                ["insight.divergent"] = analysis.IsDivergent.ToString()
            }
        }, ct).ConfigureAwait(false);

        if (analysis.IsDivergent)
        {
            _logger.LogWarning(
                "Cross-signal divergence detected: {Description}",
                analysis.DivergenceDescription);
        }

        // Also emit as a message if handler is registered (for routing to other participants)
        if (_messageHandler is not null && analysis.IsDivergent)
        {
            var message = new MessageUpdate
            {
                SenderParticipantId = _channelId,
                CreatedAt = DateTimeOffset.UtcNow,
                Role = ChatRole.System.ToString(),
                Contents =
                [
                    new TextContent(
                        $"⚠️ Signal divergence: {analysis.DivergenceDescription}")
                ]
            };

            await _messageHandler(_channelId, message, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        if (_contextSubscription is not null)
        {
            await _contextSubscription.DisposeAsync();
        }

        if (_contextLoop is not null)
        {
            try { await _contextLoop; } catch { }
        }

        if (_analysisLoop is not null)
        {
            try { await _analysisLoop; } catch { }
        }

        await _audioBuffer.DisposeAsync();
        _cts.Dispose();

        if (_disconnectedHandler is not null)
        {
            try { await _disconnectedHandler(_channelId); } catch { }
        }
    }
}
