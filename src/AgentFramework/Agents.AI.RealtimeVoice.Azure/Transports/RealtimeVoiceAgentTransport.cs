using System.Threading.Channels;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.LiveVoice.Media.Audio;
using Agents.AI.Extensions.LiveVoice.Media.Messaging;
using Agents.AI.Extensions.LiveVoice.Media.Signaling;
using Agents.AI.Extensions.LiveVoice.Media.Transcription;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.RealtimeVoice.Azure.Media.Audio;
using Agents.AI.RealtimeVoice.Azure.Models;
using Agents.AI.RealtimeVoice.Azure.VoiceAgent;
using Extensions.AI.Contents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Transports;

/// <summary>
/// Realtime voice AI agent transport that proxies audio and messages between
/// the conversation hub and a realtime AI agent (typically an <see cref="IvrAgent"/>).
/// <para>
/// Handles VAD events for presence detection and delegates utterance tracking
/// to the agent when it implements <see cref="IvrAgent"/>. All workflow
/// orchestration lives in the agent — this transport is a pure media bridge.
/// </para>
/// </summary>
public sealed class RealtimeVoiceAgentTransport : IChannelTransport, IAudioConsumer, IAudioProducer, IMessageConsumer, IMessageProducer, ISignalConsumer, ITranscriptProducer
{
    private readonly AuthorizingRealtimeAIAgent _agent;
    private readonly LiveConversationAgentSession _thread;
    private readonly RealtimeIvrWorkflowDefinition _workflow;
    private readonly IvrWorkflowState _workflowState = new();

    private readonly AgentRunOptions? _runOptions;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly PresenceDetectorService? _presenceDetector;

    private Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> _audioHandler = (_, _, _) => Task.CompletedTask;
    private Func<string, MessageUpdate, CancellationToken, Task> _messageHandler = (_, _, _) => Task.CompletedTask;
    private Func<string, TranscriptSegment, CancellationToken, Task> _transcriptHandler = (_, _, _) => Task.CompletedTask;
    private Func<string, Task>? _disconnectedHandler;

    private readonly Channel<DataContent> _inboundAudioChannel;
    private Task? _backgroundLoop;

    public RealtimeVoiceAgentTransport(
        AuthorizingRealtimeAIAgent agent,
        LiveConversationAgentSession existingThread,
        RealtimeIvrWorkflowDefinition workflow,
        AgentRunOptions? runOptions = null,
        PresenceDetectorService? presenceDetector = null,
        ILoggerFactory? loggerFactory = null)
    {
        _agent = agent;
        _thread = existingThread;
        _workflow = workflow;
        _runOptions = runOptions;
        _presenceDetector = presenceDetector;
        _logger = loggerFactory?.CreateLogger<RealtimeVoiceAgentTransport>()
                  ?? NullLogger<RealtimeVoiceAgentTransport>.Instance;

        Metadata = new ParticipantTransportMetadata
        {
            ContactId = agent.Id,
            ChannelType = CommunicationChannelType.VoiceAIAgent,
            RawIdentifier = existingThread.ActiveSessionId ?? agent.Id,
            DisplayName = agent.DisplayName,
            Role = ChannelRole.PrimaryVoice | ChannelRole.InteractiveMessaging,
            SupportsAudio = true,
            SupportsMessaging = true
        };

        _inboundAudioChannel = Channel.CreateBounded<DataContent>(new BoundedChannelOptions(500)
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
            RunAgentStreamAsync(linkedCts.Token),
            RunSendLoopAsync(linkedCts.Token)
        );

        return Task.CompletedTask;
    }

    public void SetOnAudioReceivedCallback(Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> handler) => _audioHandler = handler;
    public void SetOnMessageReceivedCallback(Func<string, MessageUpdate, CancellationToken, Task> handler) => _messageHandler = handler;
    public void SetOnDisconnected(Func<string, Task> handler) => _disconnectedHandler = handler;
    public void SetOnTranscriptReceivedCallback(Func<string, TranscriptSegment, CancellationToken, Task> handler) => _transcriptHandler = handler;

    public async Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        var dataContent = new DataContent(audioData, "audio/pcm");
        await _inboundAudioChannel.Writer.WriteAsync(dataContent, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default)
    {
        var chat = MessageUpdateExtensions.ToChatMessage(message);
        await _agent.SendMessagesToRunAsync([chat], _thread, cancellationToken).ConfigureAwait(false);
    }

    public Task SendSignalAsync(SessionSignal signal, CancellationToken cancellationToken = default)
    {
        return signal.Kind switch
        {
            //SessionSignalKind.StopAudio => _agent.StopAudioAsync(_thread, cancellationToken),
            _ => Task.CompletedTask // Other signals can be handled as needed
        };
    }


    private async Task RunSendLoopAsync(CancellationToken ct)
    {
        await foreach (var dataContent in _inboundAudioChannel.Reader.ReadAllAsync(ct))
        {
            await _agent.SendAudioToRunAsync(dataContent, _thread, ct).ConfigureAwait(false);
        }
    }

    private async Task RunAgentStreamAsync(CancellationToken cancellationToken)
    {
        var userTurnStart = DateTimeOffset.UtcNow;
        DateTimeOffset? userTurnEnd = null;
        var agentTurnStart = DateTimeOffset.UtcNow;
        DateTimeOffset? agentTurnEnd = null;

        // Resolve IvrAgent once if available — avoids repeated casts on the hot path
        var ivrAgent = _agent.GetService(typeof(AIAgent)) as IvrAgent;

        try
        {
            await foreach (var update in _agent.RunStreamingAsync(_thread, _runOptions, cancellationToken).ConfigureAwait(false))
            {
                ReadOnlyMemory<byte>? audioFrame = null;
                List<AIContent>? nonAudio = null;

                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case DataContent dc when !dc.Data.IsEmpty:
                            audioFrame = dc.Data;
                            break;

                        case RealtimeVadContent vc:
                            (nonAudio ??= []).Add(content);
                            _presenceDetector?.OnVadEvent(vc.VadEvent);

                            switch (vc.VadEvent)
                            {
                                case VadEventType.InputSpeechStarted:
                                    userTurnStart = vc.TimeStamp;
                                    break;
                                case VadEventType.InputSpeechEnded:
                                    userTurnEnd = vc.TimeStamp;
                                    break;
                                case VadEventType.OutputSpeechStarted:
                                    agentTurnStart = vc.TimeStamp;
                                    break;
                                case VadEventType.OutputSpeechEnded:
                                    agentTurnEnd = vc.TimeStamp;
                                    break;
                            }

                            break;

                        case TextContent tc when !string.IsNullOrWhiteSpace(tc.Text):
                            (nonAudio ??= []).Add(content);
                            var (role, start, end) = update.Role == ChatRole.User
                                ? (ChatRole.User, userTurnStart, userTurnEnd)
                                : (ChatRole.Assistant, agentTurnStart, agentTurnEnd);
                            ivrAgent?.RecordUtterance(role, start, end, tc);
                            break;

                        case AudioTranscriptionContent atc when !string.IsNullOrWhiteSpace(atc.Text):
                            (nonAudio ??= []).Add(content);
                            var (r, s, e) = update.Role == ChatRole.User
                                ? (ChatRole.User, userTurnStart, userTurnEnd)
                                : (ChatRole.Assistant, agentTurnStart, agentTurnEnd);
                            var transcriptSegment = new TranscriptSegment
                            {
                                Role = update.Role,
                                Text = atc.Text,
                                UtteranceStart = update.CreatedAt,
                                IsFinal = false,
                            };
                            await _transcriptHandler(ChannelId, transcriptSegment, cancellationToken).ConfigureAwait(false);
                            ivrAgent?.RecordUtterance(r, s, e, atc);
                            break;

                        default:
                            (nonAudio ??= []).Add(content);
                            break;
                    }
                }

                if (audioFrame.HasValue)
                {
                    await _audioHandler(ChannelId, audioFrame.Value, cancellationToken).ConfigureAwait(false);
                }

                if (nonAudio is { Count: > 0 })
                {
                    update.Contents = nonAudio;
                    var msg = MessageUpdateExtensions.FromAgentRunResponseUpdate(update);
                    await _messageHandler(ChannelId, msg, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Realtime voice stream failed for {ChannelId}, attempting text-only fallback", ChannelId);

            // Notify the conversation hub that audio is degraded
            var degradedMsg = new MessageUpdate
            {
                Contents = [new TextContent("I'm experiencing audio difficulties. Let me continue assisting you via text.")]
            };
            await _messageHandler(ChannelId, degradedMsg, CancellationToken.None).ConfigureAwait(false);

            // Continue processing via text-only path if available
            // This keeps the session alive rather than dropping the caller
        }
        finally
        {
            if (_disconnectedHandler is not null)
            {
                try
                {
                    await _disconnectedHandler(ChannelId);
                }
                catch
                {
                    // Ignore errors during disconnect handling
                }
            }
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

        _thread.Dispose();
        _cts.Dispose();

        if (_disconnectedHandler is not null)
        {
            try
            {
                await _disconnectedHandler(ChannelId);
            }
            catch
            {
                // Ignore
            }
        }
    }


}
