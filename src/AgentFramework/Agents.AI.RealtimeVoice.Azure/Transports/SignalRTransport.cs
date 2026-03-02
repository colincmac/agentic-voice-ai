using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.RealtimeVoice.Azure.Media.Messaging;
using Agents.AI.RealtimeVoice.Azure.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;

namespace Agents.AI.RealtimeVoice.Azure.Transports;

internal sealed class SignalRTransport : IChannelTransport, IMessageProducer
{
    private readonly IClientProxy _proxy;
    private readonly ParticipantTransportMetadata _metadata;
    private Func<string, Task>? _disconnectedHandler;

    public SignalRTransport(string channelId, ParticipantTransportMetadata metadata, IClientProxy proxy)
    {
        ChannelId = channelId;
        _metadata = metadata;
        _proxy = proxy;
    }

    public string ChannelId { get; }
    public ParticipantTransportMetadata Metadata => _metadata;

    public bool IsConnected => _proxy is not null;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default)
    {
        return _proxy.SendAsync("message", new
        {
            message.SenderParticipantId,
            message.Role,
            contents = message.Contents?.OfType<TextContent>().Select(t => t.Text).ToArray()
        }, cancellationToken);
    }

    public void SetOnDisconnected(Func<string, Task> handler)
    {
        _disconnectedHandler = handler;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disconnectedHandler is not null)
        {
            try { await _disconnectedHandler(ChannelId); } catch { }
        }
    }
}
