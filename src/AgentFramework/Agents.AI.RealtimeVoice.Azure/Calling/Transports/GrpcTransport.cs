using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;
using Agents.Realtimevoice.V1;
using Grpc.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Transports;

internal sealed class GrpcTransport : IChannelTransport
{
    private readonly IServerStreamWriter<ServerEnvelope>? _serverWriter;
    private readonly ParticipantTransportMetadata _metadata;
    private Func<string, ReadOnlyMemory<byte>, CancellationToken, Task>? _audioHandler;
    private Func<string, MessageUpdate, CancellationToken, Task>? _messageHandler;
    private Func<string, Task>? _disconnectedHandler;
    private readonly ILogger<GrpcTransport> _logger;

    public GrpcTransport(
        string channelId,
        ParticipantTransportMetadata metadata,
        IServiceProvider serviceProvider,
        IServerStreamWriter<ServerEnvelope>? serverWriter,
        ILogger<GrpcTransport>? logger = null)
    {
        ChannelId = channelId;
        _metadata = metadata;
        _serverWriter = serverWriter;
        _logger = logger ?? NullLogger<GrpcTransport>.Instance;
    }

    public string ChannelId { get; }
    public ParticipantTransportMetadata Metadata => _metadata;
    public bool IsConnected => _serverWriter is not null;
    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        if (_serverWriter is null) return Task.CompletedTask;
        var frame = new AudioFrame
        {
            SessionId = Metadata.ServerCallId ?? string.Empty,
            ChannelId = ChannelId,
            Pcm = Google.Protobuf.ByteString.CopyFrom(audioData.Span),
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        return _serverWriter.WriteAsync(new ServerEnvelope { Audio = frame }, cancellationToken);
    }

    public Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default)
    {
        if (_serverWriter is null) return Task.CompletedTask;
        var chat = new ControlMessageUpdate
        {
            SessionId = Metadata.ServerCallId ?? string.Empty,
            ChannelId = ChannelId,
            Role = message.Role ?? string.Empty,
            Text = message.Contents?.OfType<TextContent>().FirstOrDefault()?.Text ?? string.Empty,
            TimestampUnixMs = message.CreatedAt?.ToUnixTimeMilliseconds() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        return _serverWriter.WriteAsync(new ServerEnvelope { Chat = chat }, cancellationToken);
    }

    public void OnAudioReceived(Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> handler) => _audioHandler = handler;
    public void OnMessageReceived(Func<string, MessageUpdate, CancellationToken, Task> handler) => _messageHandler = handler;
    public void OnDisconnected(Func<string, Task> handler) => _disconnectedHandler = handler;

    public Task HandleInboundAsync(IAsyncStreamReader<ClientEnvelope> reader, CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            while (await reader.MoveNext(ct).ConfigureAwait(false))
            {
                var current = reader.Current;
                if (current.Audio is not null && _audioHandler is not null)
                {
                    var bytes = current.Audio.Pcm.ToByteArray();
                    await _audioHandler(ChannelId, bytes, ct).ConfigureAwait(false);
                }
                else if (current.Chat is not null && _messageHandler is not null)
                {
                    var msg = new MessageUpdate
                    {
                        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(current.Chat.TimestampUnixMs),
                        SenderParticipantId = current.Chat.ChannelId,
                        Role = current.Chat.Role,
                        Contents = [new TextContent(current.Chat.Text)]
                    };
                    await _messageHandler(ChannelId, msg, ct).ConfigureAwait(false);
                }
            }
            if (_disconnectedHandler is not null)
            {
                try { await _disconnectedHandler(ChannelId); } catch { }
            }
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disconnectedHandler is not null)
        {
            try { await _disconnectedHandler(ChannelId); } catch { }
        }
    }
}
