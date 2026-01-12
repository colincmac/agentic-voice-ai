using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;
using Azure.Communication.CallAutomation;
using Extensions.AI.Contents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DtmfTone = Extensions.AI.AudioHelpers.DtmfTone;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Transports;

public sealed class AcsWebsocketTransport : IChannelTransport
{
    private readonly WebSocket _webSocket;
    private readonly CallConnectionProperties _call;
    private readonly ILogger<AcsWebsocketTransport> _logger;
    private readonly CancellationTokenSource _cts;
    private Func<string, ReadOnlyMemory<byte>, CancellationToken, Task>? _audioHandler;
    private Func<string, MessageUpdate, CancellationToken, Task>? _messageHandler;
    private Func<string, Task>? _disconnectedHandler;
    private bool _gracefulClose;
    private Task? _backgroundLoop;
    private readonly Channel<byte[]> _inboundAudioChannel;

    public AcsWebsocketTransport(
        WebSocket webSocket,
        CallConnectionProperties callConnection,
        CancellationToken httpContextCancellation,
        ILogger<AcsWebsocketTransport>? logger = null)
    {
        _webSocket = webSocket;
        _call = callConnection;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(httpContextCancellation);
        _logger = logger ?? NullLogger<AcsWebsocketTransport>.Instance;
        Metadata = new ParticipantTransportMetadata
        {
            ContactId = _call.CallConnectionId,
            ChannelType = CommunicationChannelType.Phone,
            RawIdentifier = _call.Source.RawId,
            DisplayName = _call.SourceDisplayName,
            SupportsAudio = true,
            SupportsMessaging = true,
            SupportsVideo = false,
            SupportsScreenShare = false,
            ServerCallId = _call.ServerCallId
        };
        _inboundAudioChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(500)
        {
            SingleReader = true,
            SingleWriter = false, // Multiple participants might speak to this transport
            FullMode = BoundedChannelFullMode.DropOldest, // better to skip frames than lag
            AllowSynchronousContinuations = true
        });
    }

    public string ChannelId => Metadata.ContactId;
    public ParticipantTransportMetadata Metadata { get; }
    public bool IsConnected => _backgroundLoop is { Status: TaskStatus.Running };

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if(IsConnected) return Task.CompletedTask;
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _backgroundLoop = Task.WhenAll(
             RunReceiveLoopAsync(linkedCts.Token),
             RunSendLoopAsync(linkedCts.Token)
        );
        return Task.CompletedTask;
    }
    public void OnAudioReceived(Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> handler) => _audioHandler = handler;
    public void OnMessageReceived(Func<string, MessageUpdate, CancellationToken, Task> handler) => _messageHandler = handler;
    public void OnDisconnected(Func<string, Task> handler) => _disconnectedHandler = handler;

    public async Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        var data = audioData.ToArray();
        await _inboundAudioChannel.Writer.WriteAsync(data, cancellationToken);
    }

    public async Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default)
    {
        foreach (var content in message.Contents)
        {
            if (content is RealtimeVadContent vc && vc.VadEvent is VadEventType.InputSpeechStarted)
            {
                var stop = OutStreamingData.GetStopAudioForOutbound();
                await _webSocket.SendAsync(
                    new BinaryData(stop),
                     WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
            }
        }
    }

    private async Task RunSendLoopAsync(CancellationToken ct)
    {
        await foreach (var audioBytes in _inboundAudioChannel.Reader.ReadAllAsync(ct))
        {
            var outbound = OutStreamingData.GetAudioDataForOutbound(audioBytes);
            await _webSocket.SendAsync(new BinaryData(outbound), WebSocketMessageType.Text, true, ct);
        }
    }

    private static StreamingData? TryParse(byte[] payload)
    {
        var s = Encoding.UTF8.GetString(payload).TrimEnd('\0');
        return StreamingData.Parse(s);
    }


    private async Task RunReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var bufferPool = ArrayPool<byte>.Shared;
        byte[]? buffer = null;
        long totalBytesReceived = 0;
        long totalPacketsReceived = 0;
        var start = Stopwatch.GetTimestamp();

        try
        {
            while (!cancellationToken.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                var zero = await _webSocket.ReceiveAsync(Memory<byte>.Empty, cancellationToken);
                if (zero.MessageType == WebSocketMessageType.Close)
                {
                    _gracefulClose = true;
                    break;
                }

                buffer = bufferPool.Rent(64 * 1024);
                var result = await _webSocket.ReceiveAsync(buffer, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _gracefulClose = true;
                    break;
                }

                totalBytesReceived += result.Count;
                totalPacketsReceived++;
                var acsData = TryParse(buffer.AsSpan(0, result.Count).ToArray());

                switch (acsData)
                {
                    case AudioData audio when !audio.IsSilent && _audioHandler is not null:
                        await _audioHandler(ChannelId, audio.Data, _cts.Token);
                        // Also surface timestamp + participant id as message
                        if (_messageHandler is not null)
                        {
                            await _messageHandler(ChannelId, new MessageUpdate
                            {
                                CreatedAt = audio.Timestamp,
                                SenderParticipantId = audio.Participant?.RawId
                            }, _cts.Token);
                        }
                        break;

                    case DtmfData dtmf when _messageHandler is not null && DtmfTone.TryFromString(dtmf.Data, out var tone):
                        await _messageHandler(ChannelId, new MessageUpdate
                        {
                            CreatedAt = DateTimeOffset.UtcNow,
                            Contents = [new DtmfToneContent(tone)],
                            SenderParticipantId = ChannelId
                        }, _cts.Token);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket error on {ChannelId}", ChannelId);
        }
        finally
        {
            if (buffer is not null) bufferPool.Return(buffer, true);
            var dur = Stopwatch.GetElapsedTime(start).TotalSeconds;
            _logger.LogInformation("AcsWebsocketTransport {ChannelId} closed. Bytes={Bytes}, Packets={Packets}, Graceful={Graceful}, Duration={Dur:F2}s",
                ChannelId, totalBytesReceived, totalPacketsReceived, _gracefulClose, dur);
            if (_disconnectedHandler is not null)
            {
                try { await _disconnectedHandler(ChannelId); } catch { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        
        if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Dispose", CancellationToken.None);
            }
            catch { }
        }
        _webSocket.Dispose();
        _cts.Dispose();
        if (_disconnectedHandler is not null)
        {
            try { await _disconnectedHandler(ChannelId); } catch { }
        }
    }
}
