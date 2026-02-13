using System.Threading.Channels;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Transports;

/// <summary>
/// Realtime AI agent transport with authorization and approval workflow integration
/// </summary>
public sealed class RealtimeAIAgentTransport : IChannelTransport
{
    private readonly AuthorizingRealtimeAIAgent _agent;
    private readonly LiveConversationAgentSession _thread;
    private readonly AgentRunOptions? _runOptions;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly ICallAnalyticsService? _analyticsService;
    private readonly string? _sessionId;
    private Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> _audioHandler = (_, _, _) => Task.CompletedTask;
    private Func<string, MessageUpdate, CancellationToken, Task> _messageHandler = (_, _, _) => Task.CompletedTask;
    private Func<string, Task> _disconnectedHandler = _ => Task.CompletedTask;
    private readonly Channel<DataContent> _inboundAudioChannel;
    private Task? _backgroundLoop;

    public RealtimeAIAgentTransport(
        AuthorizingRealtimeAIAgent baseAgent,
        LiveConversationAgentSession existingThread,
        AgentRunOptions? runOptions = null,
        ILoggerFactory? loggerFactory = null,
        ICallAnalyticsService? analyticsService = null,
        string? sessionId = null)
    {
        _thread = existingThread;
        _runOptions = runOptions;
        _agent = baseAgent;
        _logger = loggerFactory?.CreateLogger<RealtimeAIAgentTransport>() ?? NullLogger<RealtimeAIAgentTransport>.Instance;
        _analyticsService = analyticsService;
        _sessionId = sessionId;

        Metadata = new ParticipantTransportMetadata
        {
            ContactId = baseAgent.Id,
            ChannelType = CommunicationChannelType.VoiceAIAgent,
            RawIdentifier = existingThread.ActiveSessionId ?? baseAgent.Id,
            DisplayName = baseAgent.DisplayName,
            Role = ChannelRole.PrimaryVoice | ChannelRole.InteractiveMessaging,
            SupportsAudio = true,
            SupportsMessaging = true
        };

        _inboundAudioChannel = Channel.CreateBounded<DataContent>(new BoundedChannelOptions(500)
        {
            SingleReader = true,
            SingleWriter = true, // Multiple participants might speak to this transport
            FullMode = BoundedChannelFullMode.DropOldest, // better to skip frames than lag
            AllowSynchronousContinuations = true
        });
    }

    public string ChannelId => Metadata.ContactId;
    public ParticipantTransportMetadata Metadata { get; }
    public bool IsConnected => _backgroundLoop is not null;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_backgroundLoop is not null) return;
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _backgroundLoop = Task.WhenAll(
             RunSingleAgentStreamAsync(linkedCts.Token),
             RunSendLoopAsync(linkedCts.Token)
        );
    }

    public void OnAudioReceived(Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> handler) => _audioHandler = handler;
    public void OnMessageReceived(Func<string, MessageUpdate, CancellationToken, Task> handler) => _messageHandler = handler;
    public void OnDisconnected(Func<string, Task> handler) => _disconnectedHandler = handler;

    public async Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        var dataContent = new DataContent(audioData.ToArray(), "audio/pcm");
        await _inboundAudioChannel.Writer.WriteAsync(dataContent, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default)
    {
        // Analyze incoming user message for health metrics (fire-and-forget)
        if (_analyticsService is not null && _sessionId is not null)
        {
            var textContent = message.Contents.OfType<TextContent>().FirstOrDefault();
            if (textContent?.Text is not null)
            {
                _ = AnalyzeUtteranceInBackgroundAsync(
                    _sessionId,
                    message.Role ?? "user",
                    textContent.Text,
                    cancellationToken);
            }
        }

        var chat = MessageUpdateExtensions.ToChatMessage(message);
        await _agent.SendMessagesToRunAsync([chat], _thread, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunSendLoopAsync(CancellationToken ct)
    {
        await foreach (var dataContent in _inboundAudioChannel.Reader.ReadAllAsync(ct))
        {
            await _agent.SendAudioToRunAsync(dataContent, _thread, ct).ConfigureAwait(false);
        }
    }

    private async Task RunSingleAgentStreamAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Use the authorizing agent which wraps the base agent with approval workflow
            await foreach (var update in _agent.RunStreamingAsync(_thread, _runOptions, cancellationToken).ConfigureAwait(false))
            {
                ReadOnlyMemory<byte>? audioFrame = null;
                List<AIContent>? nonAudio = null;

                foreach (var c in update.Contents)
                {
                    if (c is DataContent dc)
                    {
                        audioFrame = dc.Data;
                    }
                    else
                    {
                        (nonAudio ??= []).Add(c);
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

                    // Analyze agent response for health metrics (fire-and-forget)
                    if (_analyticsService is not null && _sessionId is not null)
                    {
                        var textContent = nonAudio.OfType<TextContent>().FirstOrDefault();
                        if (textContent?.Text is not null)
                        {
                            _ = AnalyzeUtteranceInBackgroundAsync(
                                _sessionId,
                                "assistant",
                                textContent.Text,
                                cancellationToken);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authorizing realtime agent run error for {ChannelId}", ChannelId);
        }
        finally
        {
            if (_disconnectedHandler is not null)
            {
                try { await _disconnectedHandler(ChannelId); } catch { }
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
            try { await _disconnectedHandler(ChannelId); } catch { }
        }
    }

    /// <summary>
    /// Analyzes an utterance in the background without blocking the main message flow.
    /// </summary>
    private async Task AnalyzeUtteranceInBackgroundAsync(
        string sessionId,
        string speaker,
        string text,
        CancellationToken cancellationToken)
    {
        if (_analyticsService is null)
        {
            return;
        }

        try
        {
            await _analyticsService.AnalyzeUtteranceAsync(sessionId, speaker, text, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to analyze utterance for session {SessionId}", sessionId);
        }
    }

}
