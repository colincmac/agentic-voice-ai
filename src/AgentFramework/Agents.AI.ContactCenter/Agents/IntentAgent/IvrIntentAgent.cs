using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Agents.AI.ContactCenter.Media.Analysis;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Media.Transcription;
using Microsoft.Agents.AI;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Shared.Diagnostics;

namespace Agents.AI.ContactCenter.Agents.IntentAgent;

/// <summary>
/// Composite NLU agent: pipes audio through an <see cref="ISpeechRecognizer"/> (Azure STT in
/// the showcase) into an <see cref="IIntentClassifier"/> (typically <see cref="ChatClientIntentClassifier"/>
/// backed by phi-4-mini-instruct on Azure Foundry). Exposes both:
/// <list type="bullet">
///   <item>The standard <see cref="AIAgent"/> request/response surface for text-only IVR
///         workflow scenarios where the utterance is already known.</item>
///   <item>An audio-streaming helper (<see cref="ClassifyAudioStreamAsync"/>) for parallel-
///         assist scenarios where this agent runs alongside a primary realtime voice agent
///         and emits intent signals on every final transcript.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The agent does not own session state — every <see cref="ClassifyAudioStreamAsync"/> call
/// creates a fresh <see cref="ISpeechRecognizer"/> via the recognizer factory and disposes
/// it when the audio stream completes. This makes the agent safe to register as a singleton.
/// </para>
/// <para>
/// When <see cref="AIAgent.RunAsync"/> is used, the agent treats the concatenated user
/// messages as the utterance and uses <see cref="IvrIntentRunOptions.ValidIntents"/> when
/// supplied, falling back to <see cref="IvrIntentAgentOptions.DefaultIntents"/>. The response
/// contains the classification as a JSON payload plus a human-readable summary.
/// </para>
/// </remarks>
public sealed class IvrIntentAgent : AIAgent
{
    private static readonly AIAgentMetadata metadata = new("intent-classifier");
    private static readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly IIntentClassifier _classifier;
    private readonly ISpeechRecognizer _speechRecognizer;
    private readonly IvrIntentAgentOptions _options;
    private readonly ILogger<IvrIntentAgent> _logger;

    /// <summary>
    /// Creates an intent agent backed by the supplied classifier.
    /// </summary>
    /// <param name="classifier">The intent classifier (typically <see cref="ChatClientIntentClassifier"/>).</param>
    /// <param name="recognizerFactory">
    /// Optional factory invoked per audio session. Required to call <see cref="ClassifyAudioStreamAsync"/>;
    /// text-only callers may omit it.
    /// </param>
    /// <param name="options">Optional agent configuration (display name, default intents, …).</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    public IvrIntentAgent(
        IIntentClassifier classifier,
        ISpeechRecognizer speechRecognizer,
        IvrIntentAgentOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _speechRecognizer = speechRecognizer;
        _options = options ?? new IvrIntentAgentOptions();
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<IvrIntentAgent>();
    }

    /// <inheritdoc/>
    protected override string? IdCore => _options.Id;

    /// <inheritdoc/>
    public override string? Name => _options.Name;

    /// <inheritdoc/>
    public override string? Description => _options.Description;

    /// <inheritdoc/>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType is null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }
        if (serviceKey is null)
        {
            if (serviceType.IsInstanceOfType(this))
            {
                return this;
            }
            if (serviceType == typeof(AIAgentMetadata))
            {
                return metadata;
            }
            if (serviceType == typeof(IIntentClassifier))
            {
                return _classifier;
            }
        }
        return base.GetService(serviceType, serviceKey);
    }

    /// <summary>
    /// Classify a single utterance against the supplied candidate intents. Convenience
    /// wrapper over the underlying <see cref="IIntentClassifier"/> so callers can use the
    /// agent as a single composition point.
    /// </summary>
    public ValueTask<IntentResult> ClassifyAsync(
        string utterance,
        IReadOnlyList<string> validIntents,
        CancellationToken cancellationToken = default)
        => _classifier.ClassifyAsync(utterance, validIntents, cancellationToken);

    /// <summary>
    /// Streams audio into a fresh <see cref="ISpeechRecognizer"/> and yields one
    /// <see cref="IvrIntentEvent"/> for every final transcript segment classified against
    /// <paramref name="validIntents"/>. Designed to run in parallel with a realtime voice
    /// agent without taking over the conversation.
    /// </summary>
    /// <param name="audioFrames">Caller audio frames (raw PCM, recognizer-defined format).</param>
    /// <param name="validIntents">Candidate intents the classifier must restrict to.</param>
    /// <param name="cancellationToken">Cancels both audio ingestion and classification.</param>
    /// <exception cref="InvalidOperationException">
    /// No recognizer factory was supplied at construction time.
    /// </exception>
    public async IAsyncEnumerable<IvrIntentEvent> ClassifyAudioStreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        IReadOnlyList<string> validIntents,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (audioFrames is null)
        {
            throw new ArgumentNullException(nameof(audioFrames));
        }

        if (validIntents is null || validIntents.Count == 0)
        {
            yield break;
        }


        var events = Channel.CreateUnbounded<IvrIntentEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var audioTask = PumpAudioAsync(audioFrames, pumpCts.Token);
        var classifyTask = ClassifyTranscriptsAsync(validIntents, events.Writer, pumpCts.Token);

        try
        {
            while (await events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (events.Reader.TryRead(out var evt))
                {
                    yield return evt;
                }
            }
        }
        finally
        {
            await pumpCts.CancelAsync().ConfigureAwait(false);
            try { await audioTask.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception ex)
            {
                _logger.LogDebug(ex, "Audio pump task faulted during shutdown");
            }
            try { await classifyTask.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception ex)
            {
                _logger.LogDebug(ex, "Classification task faulted during shutdown");
            }
            await _speechRecognizer.DisposeAsync().ConfigureAwait(false);
            pumpCts.Dispose();
        }
    }

    /// <inheritdoc/>
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        var utterance = ExtractUtterance(messages);
        var validIntents = ResolveValidIntents(options);

        if (string.IsNullOrWhiteSpace(utterance) || validIntents.Count == 0)
        {
            return BuildResponse(IntentResult.None, utterance, validIntents);
        }

        var result = await _classifier.ClassifyAsync(utterance, validIntents, cancellationToken)
            .ConfigureAwait(false);

        return BuildResponse(result, utterance, validIntents);
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Throw.IfNullOrEmpty(messages);

        
        var response = await RunCoreAsync(messages, session, options, cancellationToken)
            .ConfigureAwait(false);

        foreach (var message in response.Messages)
        {
            yield return new AgentResponseUpdate
            {
                AuthorName = Name,
                AgentId = Id,
                Role = message.Role,
                Contents = message.Contents,
                RawRepresentation = message.RawRepresentation,
            };
        }
    }

    /// <inheritdoc/>
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => new(new IvrIntentAgentSession());

    /// <inheritdoc/>
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => new(JsonSerializer.SerializeToElement<object?>(null, jsonSerializerOptions ?? serializerOptions));

    /// <inheritdoc/>
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => new(new IvrIntentAgentSession());

    private IReadOnlyList<string> ResolveValidIntents(AgentRunOptions? options)
    {
        if (options is IvrIntentRunOptions ivrOptions &&
            ivrOptions.ValidIntents is { Count: > 0 } supplied)
        {
            return supplied;
        }
        return _options.DefaultIntents ?? Array.Empty<string>();
    }

    private static string ExtractUtterance(IEnumerable<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.User)
            {
                continue;
            }
            var text = message.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }
            builder.Append(text);
        }
        return builder.ToString();
    }

    private AgentResponse BuildResponse(
        IntentResult result,
        string utterance,
        IReadOnlyList<string> candidates)
    {
        var payload = new IntentResponsePayload(
            Intent: result.IntentName,
            Confidence: result.Confidence,
            Entities: result.Entities,
            Utterance: utterance,
            Candidates: candidates);

        var json = JsonSerializer.Serialize(payload, serializerOptions);
        var summary = result.IsNone
            ? $"No intent matched for utterance: \"{utterance}\""
            : $"Intent '{result.IntentName}' (confidence {result.Confidence:F2})";

        var responseMessage = new ChatMessage(ChatRole.Assistant, new[]
        {
            (AIContent)new TextContent(summary),
            new DataContent(json, "application/json"),
        })
        {
            AuthorName = Name,
        };

        return new AgentResponse(responseMessage)
        {
            AgentId = Id,
        };
    }

    private async Task PumpAudioAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in audioFrames.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (frame.IsEmpty)
                {
                    continue;
                }
                await _speechRecognizer.WriteAudioAsync(frame, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await _speechRecognizer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Recognizer.CompleteAsync threw during audio pump shutdown");
            }
        }
    }

    private async Task ClassifyTranscriptsAsync(
        IReadOnlyList<string> validIntents,
        ChannelWriter<IvrIntentEvent> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var segment in _speechRecognizer.GetTranscriptsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!segment.IsFinal || string.IsNullOrWhiteSpace(segment.Text))
                {
                    continue;
                }

                IntentResult result;
                try
                {
                    result = await _classifier.ClassifyAsync(segment.Text, validIntents, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Intent classification failed for transcript segment; emitting IntentResult.None");
                    result = IntentResult.None;
                }

                await writer.WriteAsync(
                    new IvrIntentEvent(segment, result, DateTimeOffset.UtcNow),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private sealed record IntentResponsePayload(
        string? Intent,
        double Confidence,
        IReadOnlyDictionary<string, string>? Entities,
        string Utterance,
        IReadOnlyList<string> Candidates);

    private sealed class IvrIntentAgentSession : AgentSession;
}
