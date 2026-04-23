using System.Threading.Channels;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.RealtimeVoice.Azure.Calling;
using Agents.AI.Extensions.LiveVoice.Media.Messaging;
using Agents.AI.RealtimeVoice.Azure.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.A2A;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Transports;

/// <summary>
/// Transport that wraps an A2A (Agent-to-Agent) <see cref="AIAgent"/> as an
/// <see cref="IChannelTransport"/> participant in a conversation session.
/// <para>
/// Inbound messages received via <see cref="SendMessageAsync"/> are forwarded to the
/// A2A agent. The agent's responses are delivered back through the session router
/// and simultaneously published to the <see cref="HubSessionEventBus"/> as
/// <see cref="HubSessionEventKind.AgentInsight"/> events so that other participants
/// (e.g., the primary voice agent) can consume them as additional context.
/// </para>
/// <para>
/// When a <see cref="HubSessionEventBus"/> is provided the transport also subscribes
/// to context events (transcripts, chat messages) and feeds them to the A2A agent
/// automatically, enabling cross-agent awareness without explicit wiring.
/// </para>
/// </summary>
public sealed class A2AAgentTransport : IChannelTransport, IMessageConsumer, IMessageProducer
{
    private readonly AIAgent _agent;
    private readonly AgentSession _thread;
    private readonly AgentRunOptions? _runOptions;
    private readonly HubSessionEventBus? _eventBus;
    private readonly ILogger _logger;

    private Func<string, MessageUpdate, CancellationToken, Task>? _messageHandler;
    private Func<string, Task>? _disconnected;
    private Task? _messageLoop;
    private Task? _contextLoop;
    private SessionContextSubscription? _contextSubscription;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<ChatMessage> _inboundMessages = Channel.CreateUnbounded<ChatMessage>();

    public A2AAgentTransport(
        AIAgent agent,
        AgentSession thread,
        AgentRunOptions? runOptions = null,
        HubSessionEventBus? eventBus = null,
        ILoggerFactory? loggerFactory = null)
    {
        _agent = agent;
        _thread = thread;
        _runOptions = runOptions;
        _eventBus = eventBus;
        _logger = loggerFactory?.CreateLogger<A2AAgentTransport>()
                  ?? NullLogger<A2AAgentTransport>.Instance;

        Metadata = new ParticipantTransportMetadata
        {
            ContactId = agent.Id ?? Guid.NewGuid().ToString(),
            ChannelType = CommunicationChannelType.A2AAgent,
            RawIdentifier = agent.Id ?? "a2a-agent",
            DisplayName = agent.Name ?? "A2A Agent",
            SupportsAudio = false,
            SupportsMessaging = true,
            Role = ChannelRole.AgentToAgent
        };
    }

    public string ChannelId => Metadata.ContactId;
    public ParticipantTransportMetadata Metadata { get; }
    public bool IsConnected => _messageLoop is not null;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_messageLoop is not null)
        {
            return Task.CompletedTask;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _messageLoop = ProcessMessagesAsync(linked.Token);

        if (_eventBus is not null)
        {
            _contextSubscription = _eventBus.Subscribe(e =>
                e.SourceParticipantId != ChannelId &&
                e.Kind is HubSessionEventKind.Transcript or HubSessionEventKind.ChatMessage);
            _contextLoop = ProcessContextEventsAsync(_contextSubscription, linked.Token);
        }

        _logger.LogInformation("A2A transport {ChannelId} connected for agent {AgentName}", ChannelId, _agent.Name);

        return Task.CompletedTask;
    }

    public void SetOnMessageReceivedCallback(Func<string, MessageUpdate, CancellationToken, Task> handler) => _messageHandler = handler;
    public void SetOnDisconnected(Func<string, Task> handler) => _disconnected = handler;

    public async Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default)
    {
        var chat = MessageUpdateExtensions.ToChatMessage(message);
        await _inboundMessages.Writer.WriteAsync(chat, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessMessagesAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _inboundMessages.Reader.ReadAllAsync(ct))
            {
                await RunAgentAndPublishAsync(message, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A2A message loop failed for {ChannelId}", ChannelId);
        }
        finally
        {
            if (_disconnected is not null)
            {
                try { await _disconnected(ChannelId); } catch { }
            }
        }
    }

    private async Task ProcessContextEventsAsync(SessionContextSubscription subscription, CancellationToken ct)
    {
        try
        {
            await foreach (var contextEvent in subscription.ReadAllAsync(ct))
            {
                if (contextEvent.Payload is not string text || string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var role = contextEvent.Kind is HubSessionEventKind.Transcript
                    ? ChatRole.User
                    : ChatRole.Assistant;

                var chatMessage = new ChatMessage(role, text)
                {
                    AuthorName = contextEvent.SourceParticipantId
                };

                await RunAgentAndPublishAsync(chatMessage, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A2A context loop failed for {ChannelId}", ChannelId);
        }
    }

    private async Task RunAgentAndPublishAsync(ChatMessage message, CancellationToken ct)
    {
        var response = await _agent.RunAsync(
            message,
            _thread,
            _runOptions,
            ct).ConfigureAwait(false);

        foreach (var msg in MessageUpdateExtensions.FromAgentResponse(response))
        {
            if (_messageHandler is not null)
            {
                await _messageHandler(ChannelId, msg, ct).ConfigureAwait(false);
            }

            if (_eventBus is not null)
            {
                var responseText = msg.Contents.OfType<TextContent>().FirstOrDefault()?.Text;
                if (!string.IsNullOrWhiteSpace(responseText))
                {
                    await _eventBus.PublishAsync(new SessionContextEvent
                    {
                        EventId = $"a2a_{ChannelId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                        Kind = HubSessionEventKind.AgentInsight,
                        SourceParticipantId = ChannelId,
                        Payload = responseText
                    }, ct).ConfigureAwait(false);
                }
            }
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

        if (_messageLoop is not null)
        {
            try { await _messageLoop; } catch { }
        }

        _cts.Dispose();
    }
}
