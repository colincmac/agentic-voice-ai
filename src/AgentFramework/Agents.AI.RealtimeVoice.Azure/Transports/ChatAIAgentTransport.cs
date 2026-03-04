using System.Threading.Channels;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.Extensions.LiveVoice.Media.Messaging;
using Agents.AI.RealtimeVoice.Azure.Models;
using Agents.AI.RealtimeVoice.Azure.VoiceAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.RealtimeVoice.Azure.Transports;

public sealed class ChatAIAgentTransport : IChannelTransport, IMessageConsumer, IMessageProducer
{
    private readonly AIAgent _agent;
    private readonly AgentThread _thread;
    private readonly AgentRunOptions? _runOptions;
    private readonly PresenceDetectorService? _presenceDetector;
    private Func<string, MessageUpdate, CancellationToken, Task>? _messageHandler;
    private Func<string, Task>? _disconnected;
    private Task? _loop;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<ChatMessage> _chatMessageChannel = Channel.CreateUnbounded<ChatMessage>();

    public ChatAIAgentTransport(
        AIAgent agent,
        AgentThread thread,
        AgentRunOptions? runOptions = null,
        PresenceDetectorService? presenceDetector = null)
    {
        _agent = agent;
        _thread = thread;
        _runOptions = runOptions;
        _presenceDetector = presenceDetector;
        Metadata = new ParticipantTransportMetadata
        {
            ContactId = agent.Id ?? Guid.NewGuid().ToString(),
            ChannelType = CommunicationChannelType.ChatAIAgent,
            RawIdentifier = agent.Id ?? "chat-agent",
            DisplayName = agent.Name ?? "Chat Agent",
            SupportsAudio = false,
            SupportsMessaging = true
        };
    }

    public string ChannelId => Metadata.ContactId;
    public ParticipantTransportMetadata Metadata { get; }
    public bool IsConnected => _loop is not null;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null) return Task.CompletedTask;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _loop = RunAsync(linked.Token);
        return Task.CompletedTask;
    }

    public void SetOnMessageReceivedCallback(Func<string, MessageUpdate, CancellationToken, Task> handler) => _messageHandler = handler;
    public void SetOnDisconnected(Func<string, Task> handler) => _disconnected = handler;

    public async Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default)
    {
        var preview = message.Contents.OfType<TextContent>().FirstOrDefault()?.Text;
        _presenceDetector?.OnChatMessageReceived(preview);

        var chat = MessageUpdateExtensions.ToChatMessage(message);
        await _chatMessageChannel.Writer.WriteAsync(chat, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var update in _chatMessageChannel.Reader.ReadAllAsync(ct))
            {
                if (_messageHandler is null) continue;
                var response = await _agent.RunAsync(
                    update,
                    _thread,
                    _runOptions,
                    ct).ConfigureAwait(false);
                foreach (var msg in MessageUpdateExtensions.FromAgentRunResponse(response))
                {
                    await _messageHandler(ChannelId, msg, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_disconnected is not null)
            {
                try { await _disconnected(ChannelId); } catch { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop; } catch { }
        }
    }
}
