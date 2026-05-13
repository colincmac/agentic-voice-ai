using System.Threading.Channels;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.Extensions.LiveVoice.Media.Audio;
using Agents.AI.Extensions.LiveVoice.Media.Messaging;
using Agents.AI.Extensions.LiveVoice.Media.Transcription;
using Agents.AI.RealtimeVoice.Azure.Models;
using Agents.AI.RealtimeVoice.Azure.VoiceAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Transports;

/// <summary>
/// Transport that bridges audio and text via STT → AI Agent → TTS pipeline.
/// Used for Tier 1 (standard chat completion) and Tier 2 (small language model) degradation.
/// <para>
/// Unlike <see cref="RealtimeVoiceAgentTransport"/> which uses a persistent WebSocket to
/// the OpenAI Realtime API, this transport decomposes the audio pipeline into discrete stages:
/// inbound audio → <see cref="ISpeechRecognizer"/> → text → <see cref="AIAgent.RunAsync"/> →
/// response text → <see cref="ISpeechSynthesizer"/> → outbound audio.
/// </para>
/// </summary>
public sealed class SttTtsAgentTransport : IChannelTransport, IAudioConsumer, IAudioProducer, IMessageConsumer, IMessageProducer, ITranscriptProducer
{
    private readonly AIAgent _agent;
    private readonly AgentSession _session;
    private readonly ISpeechRecognizer _recognizer;
    private readonly ISpeechSynthesizer _synthesizer;
    private readonly AgentRunOptions? _runOptions;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly PresenceDetectorService? _presenceDetector;

    private Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> _audioHandler = (_, _, _) => Task.CompletedTask;
    private Func<string, MessageUpdate, CancellationToken, Task> _messageHandler = (_, _, _) => Task.CompletedTask;
    private Func<string, TranscriptSegment, CancellationToken, Task> _transcriptHandler = (_, _, _) => Task.CompletedTask;
    private Func<string, Task>? _disconnectedHandler;

    private readonly Channel<ReadOnlyMemory<byte>> _inboundAudioChannel;
    private Task? _backgroundLoop;

    public SttTtsAgentTransport(
        AIAgent agent,
        AgentSession session,
        ISpeechRecognizer recognizer,
        ISpeechSynthesizer synthesizer,
        AgentRunOptions? runOptions = null,
        PresenceDetectorService? presenceDetector = null,
        ILoggerFactory? loggerFactory = null)
    {
        _agent = agent;
        _session = session;
        _recognizer = recognizer;
        _synthesizer = synthesizer;
        _runOptions = runOptions;
        _presenceDetector = presenceDetector;
        _logger = loggerFactory?.CreateLogger<SttTtsAgentTransport>()
                  ?? NullLogger<SttTtsAgentTransport>.Instance;

        Metadata = new ParticipantTransportMetadata
        {
            ContactId = agent.Id ?? Guid.NewGuid().ToString(),
            ChannelType = CommunicationChannelType.VoiceAIAgent,
            RawIdentifier = agent.Id ?? "stt-tts-agent",
            DisplayName = agent.Name ?? "STT/TTS Agent",
            Role = ChannelRole.PrimaryVoice | ChannelRole.InteractiveMessaging,
            SupportsAudio = true,
            SupportsMessaging = true
        };

        _inboundAudioChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(500)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = true
        });
    }

    public string ChannelId => Metadata.ContactId;
    public ParticipantTransportMetadata Metadata { get; }
    public bool IsConnected => _backgroundLoop is not null;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_backgroundLoop is not null)
        {
            return Task.CompletedTask;
        }

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _backgroundLoop = Task.WhenAll(
            RunSttPipelineAsync(linkedCts.Token),
            RunRecognitionLoopAsync(linkedCts.Token)
        );

        return Task.CompletedTask;
    }

    public void SetOnAudioReceivedCallback(Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> handler) => _audioHandler = handler;
    public void SetOnMessageReceivedCallback(Func<string, MessageUpdate, CancellationToken, Task> handler) => _messageHandler = handler;
    public void SetOnTranscriptReceivedCallback(Func<string, TranscriptSegment, CancellationToken, Task> handler) => _transcriptHandler = handler;
    public void SetOnDisconnected(Func<string, Task> handler) => _disconnectedHandler = handler;

    public async Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        await _inboundAudioChannel.Writer.WriteAsync(audioData, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default)
    {
        var textContent = message.Contents.OfType<TextContent>().FirstOrDefault();
        if (textContent?.Text is null)
        {
            return;
        }

        _presenceDetector?.OnChatMessageReceived(textContent.Text);

        var chat = MessageUpdateExtensions.ToChatMessage(message);
        var response = await _agent.RunAsync(chat, _session, _runOptions, cancellationToken).ConfigureAwait(false);

        foreach (var msg in MessageUpdateExtensions.FromAgentResponse(response))
        {
            await _messageHandler(ChannelId, msg, cancellationToken).ConfigureAwait(false);
        }

        // Synthesize audio for the response text
        var responseText = response.Messages
            .SelectMany(m => m.Contents.OfType<TextContent>())
            .Select(tc => tc.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

        if (responseText is not null)
        {
            await SynthesizeAndSendAudioAsync(responseText, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Continuously reads inbound audio from the channel and feeds it to the recognizer.
    /// </summary>
    private async Task RunSttPipelineAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var audioData in _inboundAudioChannel.Reader.ReadAllAsync(cancellationToken))
            {
                await _recognizer.WriteAudioAsync(audioData, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        finally
        {
            await _recognizer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Listens for recognized transcript segments and processes them through the AI agent.
    /// Only processes final segments to avoid duplicate agent calls from interim hypotheses.
    /// </summary>
    private async Task RunRecognitionLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var segment in _recognizer.GetTranscriptsAsync(cancellationToken).ConfigureAwait(false))
            {
                // Emit transcript for conversation context
                await _transcriptHandler(ChannelId, segment, cancellationToken).ConfigureAwait(false);

                // Only process final (committed) segments through the agent
                if (!segment.IsFinal || string.IsNullOrWhiteSpace(segment.Text))
                {
                    continue;
                }

                _presenceDetector?.OnChatMessageReceived(segment.Text);

                _logger.LogDebug("Processing recognized utterance: {Text}", segment.Text);

                var userMessage = new ChatMessage(ChatRole.User, segment.Text);
                AgentResponse response;

                try
                {
                    response = await _agent.RunAsync(userMessage, _session, _runOptions, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Agent invocation failed for utterance in STT/TTS transport {ChannelId}", ChannelId);

                    continue;
                }

                // Emit agent response as messages
                foreach (var msg in MessageUpdateExtensions.FromAgentResponse(response))
                {
                    await _messageHandler(ChannelId, msg, cancellationToken).ConfigureAwait(false);
                }

                // Synthesize and stream agent response audio
                var responseText = response.Messages
                    .SelectMany(m => m.Contents.OfType<TextContent>())
                    .Select(tc => tc.Text)
                    .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

                if (responseText is not null)
                {
                    await SynthesizeAndSendAudioAsync(responseText, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "STT/TTS recognition loop failed for {ChannelId}", ChannelId);
        }
        finally
        {
            if (_disconnectedHandler is not null)
            {
                try { await _disconnectedHandler(ChannelId); } catch { }
            }
        }
    }

    /// <summary>
    /// Streams synthesized audio frames to the audio handler as they become available.
    /// </summary>
    private async Task SynthesizeAndSendAudioAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var audioFrame in _synthesizer.SynthesizeAsync(text, SynthesizerInputFormat.SSML, cancellationToken).ConfigureAwait(false))
            {
                await _audioHandler(ChannelId, audioFrame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TTS synthesis failed for {ChannelId}", ChannelId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        if (_backgroundLoop is not null)
        {
            try
            {
                await _backgroundLoop.ConfigureAwait(false);
            }
            catch
            {
                // Ignore exceptions during disposal
            }
        }

        _cts.Dispose();
        await _recognizer.DisposeAsync().ConfigureAwait(false);

        if (_disconnectedHandler is not null)
        {
            try { await _disconnectedHandler(ChannelId); } catch { }
        }
    }
}
