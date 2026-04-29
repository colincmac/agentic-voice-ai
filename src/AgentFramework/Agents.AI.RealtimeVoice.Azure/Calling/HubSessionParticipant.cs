using System.Collections.Concurrent;
using System.Security.Claims;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.Extensions.LiveVoice.Media;
using Agents.AI.Extensions.LiveVoice.Media.Audio;
using Agents.AI.Extensions.LiveVoice.Media.Messaging;
using Agents.AI.RealtimeVoice.Azure.Media;
using Agents.AI.RealtimeVoice.Azure.Media.Audio;
using Agents.AI.RealtimeVoice.Azure.Models;
using Agents.AI.RealtimeVoice.Azure.Transports;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

/// <summary>
/// Represents a participant in a conversation session.
/// A participant has an identity and zero or more transports
/// through which they send/receive audio and messages.
/// This is purely a transport container — it does not implement <see cref="IChannelTransport"/> itself.
/// Implements <see cref="IConnectionMetrics"/> so operators can estimate per-instance
/// memory footprint and make scaling decisions.
/// </summary>
public sealed class HubSessionParticipant : IAsyncDisposable, IConnectionMetrics
{
    private readonly IServiceScope _participantScope;
    private readonly ConcurrentDictionary<string, TransportBinding> _transports = new();
    private readonly RawMediaStreamChannel _outboundAudio;
    private Func<string, ReadOnlyMemory<byte>, CancellationToken, Task>? _audioHandler;
    private Func<string, MessageUpdate, CancellationToken, Task>? _messageHandler;
    private Func<string, Task>? _disconnectedHandler;
    private readonly string _participantId;
    private string? _displayName;
    private ParticipantTransportMetadata? _cachedMetadata;
    private readonly ClaimsPrincipal? _cachedPrincipal;
    private int _disposed;

    public HubSessionParticipant(IServiceScope participantScope, string participantId, string? displayName = null)
    {
        _participantScope = participantScope;
        _participantId = participantId;
        _displayName = displayName;
        _cachedPrincipal = new ClaimsPrincipal();
        ConnectedAt = DateTimeOffset.UtcNow;
        _outboundAudio = new RawMediaStreamChannel(new RawMediaStreamChannelOptions
        {
            Capacity = 64 * 1024,
            ChunkSize = 640,
        });
    }

    /// <summary>
    /// Participant-level channel identifier used for routing.
    /// </summary>
    public string ChannelId => ParticipantId;
    public string ParticipantId => _participantId;
    public bool IsConnected { get; private set; }
    public string? DisplayName { get => _displayName; set => _displayName = value; }
    public string? UserIdentifier { get; set; }
    public IReadOnlyList<IChannelTransport> Transports => [.. _transports.Values.Select(b => b.Transport)];

    // --- IConnectionMetrics ---

    /// <inheritdoc />
    public long AudioBufferBytes => _outboundAudio.BufferedBytes;

    /// <inheritdoc />
    public long MessageBufferBytes => 0; // messages are forwarded inline, not buffered

    /// <inheritdoc />
    public long TotalBufferedBytes => AudioBufferBytes + MessageBufferBytes;

    /// <inheritdoc />
    public int ActiveSubscriptions => _outboundAudio.ConsumerCount;

    /// <inheritdoc />
    public long TotalAudioBytesWritten => _outboundAudio.TotalBytesWritten;

    /// <inheritdoc />
    public long TotalAudioBytesDistributed => _outboundAudio.TotalBytesDistributed;

    /// <inheritdoc />
    public DateTimeOffset ConnectedAt { get; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        foreach (var binding in _transports.Values)
        {
            await binding.Transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        IsConnected = true;
    }

    /// <summary>
    /// Aggregated claims from all underlying transports.
    /// </summary>
    public ClaimsPrincipal? User => _cachedPrincipal?.Clone();

    /// <summary>
    /// Aggregated metadata derived from all underlying transports.
    /// Cached and invalidated on transport add/remove.
    /// </summary>
    public ParticipantTransportMetadata Metadata
    {
        get
        {
            var cached = _cachedMetadata;
            if (cached is not null)
            {
                return cached;
            }

            cached = BuildMetadata();
            _cachedMetadata = cached;

            return cached;
        }
    }

    /// <summary>
    /// Writes audio to the outbound broadcast channel. All audio-capable transports
    /// receive frames via their pump tasks.
    /// </summary>
    public async Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        await _outboundAudio.WriteAsync(audioData, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a message to all messaging-capable transports attached to this participant.
    /// </summary>
    public async Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default)
    {
        foreach (var binding in _transports.Values)
        {
            if (binding.Transport is IMessageConsumer producer)
            {
                try
                {
                    await producer.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Individual transport failure should not block other transports
                }
            }
        }
    }

    public void OnAudioReceived(Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> handler) => _audioHandler = handler;
    public void OnMessageReceived(Func<string, MessageUpdate, CancellationToken, Task> handler) => _messageHandler = handler;
    public void OnDisconnected(Func<string, Task> handler) => _disconnectedHandler = handler;

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceKey is null ? _participantScope.ServiceProvider.GetService(serviceType) : _participantScope.ServiceProvider.GetKeyedService(serviceType, serviceKey);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            foreach (var binding in _transports.Values)
            {
                try { await binding.DisposeAsync(); } catch { }
            }
            _transports.Clear();
            await _outboundAudio.DisposeAsync();
            _participantScope.Dispose();
        }
        finally
        {
            IsConnected = false;
            if (_disconnectedHandler is not null)
            {
                try { await _disconnectedHandler(ChannelId); } catch { }
            }
        }
    }

    private Task OnAudioReceivedCore(string channelId, ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken)
    {
        if (_audioHandler is null)
        {
            return Task.CompletedTask;
        }

        return _audioHandler(ChannelId, audioData, cancellationToken);
    }

    private Task OnMessageReceivedCore(string channelId, MessageUpdate update, CancellationToken cancellationToken)
    {
        if (_messageHandler is null)
        {
            return Task.CompletedTask;
        }

        return _messageHandler(ChannelId, update, cancellationToken);
    }

    public async Task AddTransportAsync(IChannelTransport transport, CancellationToken cancellationToken = default)
    {
        transport.SetOnDisconnected(id => RemoveTransport(id, alreadyDisposed: true));

        if (transport is IMessageProducer messageConsumer)
        {
            messageConsumer.SetOnMessageReceivedCallback(OnMessageReceivedCore);
        }

        if (transport is IAudioProducer audioConsumer)
        {
            audioConsumer.SetOnAudioReceivedCallback(OnAudioReceivedCore);
        }

        transport.SetOnDisconnected(id => RemoveTransport(id));
        await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var subscription = _outboundAudio.Subscribe();
        var pumpTask = transport is IAudioConsumer audioProducer
            ? Task.Run(() => PumpAudioToTransportAsync(audioProducer, transport, subscription, cancellationToken), cancellationToken)
            : Task.CompletedTask;

        var binding = new TransportBinding(transport, subscription, pumpTask);
        _transports[transport.ChannelId] = binding;
        InvalidateMetadataCache();
    }

    public async Task<bool> RemoveTransport(string transportId, bool alreadyDisposed = false)
    {
        if (!_transports.TryRemove(transportId, out var binding))
        {
            return false;
        }

        InvalidateMetadataCache();

        if (!alreadyDisposed)
        {
            await binding.DisposeAsync();
        }
        else
        {
            await binding.Subscription.DisposeAsync();
        }

        return true;
    }

    private void InvalidateMetadataCache() => _cachedMetadata = null;

    private ParticipantTransportMetadata BuildMetadata()
    {
        var bindings = _transports.Values.ToArray();
        if (bindings.Length == 0)
        {
            return new ParticipantTransportMetadata
            {
                ContactId = ParticipantId,
                ChannelType = CommunicationChannelType.Unknown,
                RawIdentifier = ParticipantId,
                DisplayName = DisplayName,
                SupportsAudio = false,
                SupportsMessaging = false
            };
        }

        var first = bindings[0].Transport;

        return new ParticipantTransportMetadata
        {
            ContactId = ParticipantId,
            ChannelType = first.Metadata.ChannelType,
            RawIdentifier = ParticipantId,
            DisplayName = DisplayName ?? first.Metadata.DisplayName,
            Role = bindings.Aggregate(ChannelRole.None, (acc, b) => acc | b.Transport.Metadata.Role),
            SupportsAudio = bindings.Any(b => b.Transport is IAudioConsumer or IAudioProducer),
            SupportsMessaging = bindings.Any(b => b.Transport is IMessageConsumer or IMessageProducer),
            SupportsVideo = bindings.Any(b => b.Transport.Metadata.SupportsVideo),
            SupportsScreenShare = bindings.Any(b => b.Transport.Metadata.SupportsScreenShare)
        };
    }

    private static async Task PumpAudioToTransportAsync(
        IAudioConsumer audioProducer,
        IChannelTransport transport,
        RawMediaPipeSubscription subscription,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in subscription.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!transport.IsConnected)
                {
                    break;
                }

                try
                {
                    await audioProducer.SendAudioAsync(chunk, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // Individual frame send failure; continue pumping
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    private sealed record TransportBinding(
        IChannelTransport Transport,
        RawMediaPipeSubscription Subscription,
        Task PumpTask) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Subscription.DisposeAsync();

            try
            {
                await PumpTask.ConfigureAwait(false);
            }
            catch
            {
                // Pump may have already faulted
            }

            await Transport.DisposeAsync();
        }
    }
}

