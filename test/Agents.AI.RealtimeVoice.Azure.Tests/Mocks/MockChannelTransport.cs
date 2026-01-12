using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;
using Agents.AI.RealtimeVoice.Azure.Calling.Transports;

namespace Agents.AI.RealtimeVoice.Azure.Tests.Mocks;

/// <summary>
/// Mock implementation of IChannelTransport for testing
/// </summary>
public class MockChannelTransport : IChannelTransport
{
    private Func<string, ReadOnlyMemory<byte>, CancellationToken, Task>? _audioHandler;
    private Func<string, MessageUpdate, CancellationToken, Task>? _messageHandler;
    private Func<string, Task>? _disconnectedHandler;

    public string ChannelId { get; }
    public ParticipantTransportMetadata Metadata { get; }
    public bool IsConnected { get; set; } = true;

    // Tracking fields for assertions
    public List<ReadOnlyMemory<byte>> ReceivedAudio { get; } = new();
    public List<MessageUpdate> ReceivedMessages { get; } = new();
    public int AudioCallCount { get; private set; }
    public int MessageCallCount { get; private set; }
    public bool WasStarted { get; private set; }
    public bool WasDisposed { get; private set; }

    public MockChannelTransport(string channelId, ParticipantTransportMetadata? metadata = null)
    {
        ChannelId = channelId;
        Metadata = metadata ?? new ParticipantTransportMetadata
        {
            ContactId = channelId,
            ChannelType = CommunicationChannelType.Unknown,
            RawIdentifier = channelId,
            DisplayName = $"Mock {channelId}",
            SupportsAudio = true,
            SupportsMessaging = true
        };
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        WasStarted = true;
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        AudioCallCount++;
        ReceivedAudio.Add(audioData);
        return Task.CompletedTask;
    }

    public Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default)
    {
        MessageCallCount++;
        ReceivedMessages.Add(message);
        return Task.CompletedTask;
    }

    public void OnAudioReceived(Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> handler)
    {
        _audioHandler = handler;
    }

    public void OnMessageReceived(Func<string, MessageUpdate, CancellationToken, Task> handler)
    {
        _messageHandler = handler;
    }

    public void OnDisconnected(Func<string, Task> handler)
    {
        _disconnectedHandler = handler;
    }

    // Helper methods to simulate inbound data
    public async Task SimulateInboundAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken ct = default)
    {
        if (_audioHandler is not null)
        {
            await _audioHandler(ChannelId, audioData, ct);
        }
    }

    public async Task SimulateInboundMessageAsync(MessageUpdate message, CancellationToken ct = default)
    {
        if (_messageHandler is not null)
        {
            await _messageHandler(ChannelId, message, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!WasDisposed)
        {
            WasDisposed = true;
            IsConnected = false;
            if (_disconnectedHandler is not null)
            {
                await _disconnectedHandler(ChannelId);
            }
        }
    }
}
