using System.Buffers;
using System.Threading.Channels;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Media.Messaging;
using Agents.AI.ContactCenter.Models;
using Extensions.AI.Contents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Showcase.ConsolePlayground;

public class TestCallParticipant : IAudioConsumer, IAudioProducer, IMessageConsumer, IMessageProducer
{
    private readonly ILogger<TestCallParticipant>? _logger;
    private SpeakerOutput? _speakerOutput;
    private Func<string, Task>? _disconnectedHandler;

    public TestCallParticipant(
        string? name = null,
        ILogger<TestCallParticipant>? logger = null)
    {
        _logger = logger;
        _speakerOutput = new SpeakerOutput();
    }

    public string ChannelId => "test";
    public bool IsConnected => true;

    public ParticipantTransportMetadata Metadata => new()
    {
        RawIdentifier = "Test",
        ChannelType = CommunicationChannelType.Unknown,
        ContactId = ChannelId,
        DisplayName = "Test Participant",
        SupportsAudio = true,
        SupportsMessaging = true
    };

    public async Task WriteOutboundAsync(RawMediaStreamChannel audioOutput, MessageUpdateChannel messageOutput, CancellationToken cancellationToken = default)
    {
        using var microphoneStream = MicrophoneAudioStream.Start();
        if (microphoneStream is null)
        {
            _logger?.LogWarning("Microphone stream not initialized");
            return;
        }
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await microphoneStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead == 0)
                {
                    await Task.Delay(10, cancellationToken);
                    continue;
                }
                await audioOutput.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public async Task WriteInboundAsync(RawMediaPipeSubscription audioInput, ChannelReader<MessageUpdate> messageInput, CancellationToken cancellationToken = default)
    {
        _speakerOutput ??= new SpeakerOutput();
        var audioTask = Task.Run(async () =>
        {
            await foreach (var audioData in audioInput.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var dc = new DataContent(audioData.ToArray(), "audio/wav");
                _speakerOutput.EnqueueForPlayback(dc);
            }
        }, cancellationToken);
        var messageTask = Task.Run(async () =>
        {
            await foreach (var message in messageInput.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var content in message.Contents)
                {
                    if (content is DataContent dc) _speakerOutput.EnqueueForPlayback(dc);
                    if (content is RealtimeVadContent vc && vc.VadEvent == VadEventType.InputSpeechStarted) _speakerOutput.ClearPlayback();
                }
            }
        }, cancellationToken);
        await Task.WhenAll(audioTask, messageTask);
    }

    public void ClearPlayback() => _speakerOutput?.ClearPlayback();

    public Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void SetOnAudioReceivedCallback(Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> handler) { }
    public void SetOnMessageReceivedCallback(Func<string, MessageUpdate, CancellationToken, Task> handler) { }
    public void SetOnDisconnected(Func<string, Task> handler) => _disconnectedHandler = handler;
    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public async ValueTask DisposeAsync()
    {
        if (_disconnectedHandler is not null) { try { await _disconnectedHandler(ChannelId); } catch { } }
    }

}

