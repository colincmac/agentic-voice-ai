using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Channels;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Transports;

public sealed class ChatAIAgentTransport : IChannelTransport
{
    private readonly AIAgent _agent;
    private readonly AgentThread _thread;
    private readonly AgentRunOptions? _runOptions;
    private Func<string, MessageUpdate, CancellationToken, Task>? _messageHandler;
    private Func<string, Task>? _disconnected;
    private Task? _loop;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<ChatMessage> _chatMessageChannel = Channel.CreateUnbounded<ChatMessage>();
    public ChatAIAgentTransport(
        AIAgent agent,
        AgentThread thread,
        AgentRunOptions? runOptions = null)
    {
        _agent = agent;
        _thread = thread;
        _runOptions = runOptions;
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

    public void OnAudioReceived(Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> handler) { /* not used */ }
    public void OnMessageReceived(Func<string, MessageUpdate, CancellationToken, Task> handler) => _messageHandler = handler;
    public void OnDisconnected(Func<string, Task> handler) => _disconnected = handler;

    public Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
        => Task.CompletedTask; // no audio

    public async Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default)
    {
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
