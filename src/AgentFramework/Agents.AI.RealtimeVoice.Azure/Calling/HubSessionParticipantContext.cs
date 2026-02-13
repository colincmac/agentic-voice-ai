using System.Collections.Concurrent;
using System.Security.Claims;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;
using Agents.AI.RealtimeVoice.Azure.Calling.Transports;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

public sealed class HubSessionParticipantContext : IScopedChannelTransport
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
    private int _disposed;
    private ClaimsPrincipalServiceProvider ServiceProvider => new(_participantScope.ServiceProvider, GetClaimsPrincipal);

    public HubSessionParticipantContext(IServiceScope participantScope, string participantId, string? displayName = null)
    {
        _participantScope = participantScope;
        _participantId = participantId;
        _displayName = displayName;
        _outboundAudio = new RawMediaStreamChannel(new RawMediaStreamChannelOptions
        {
            Capacity = 64 * 1024,
            ChunkSize = 640,
        });
    }

    public string ChannelId => ParticipantId;
    public string ParticipantId => _participantId;
    public bool IsConnected { get; private set; }
    public string? DisplayName { get => _displayName; set => _displayName = value; }
    public string? UserIdentifier { get; set; }
    public IReadOnlyList<IScopedChannelTransport> Transports => [.. _transports.Values.Select(b => b.Transport)];

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        foreach (var binding in _transports.Values)
        {
            await binding.Transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        IsConnected = true;
    }

    public ClaimsPrincipal User
    {
        get
        {
            var transports = Transports;
            if (transports.Count == 0)
            {
                return new ClaimsPrincipal();
            }

            return transports.Select(t => t.User).Aggregate((current, next) =>
            {
                var combined = new ClaimsPrincipal(current);
                combined.AddIdentities(next.Identities);

                return combined;
            });
        }
    }

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

    public async Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        await _outboundAudio.WriteAsync(audioData, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default)
    {
        foreach (var binding in _transports.Values)
        {
            if (binding.Transport.Metadata.SupportsMessaging)
            {
                try
                {
                    await binding.Transport.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
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
        return serviceKey is null ? ServiceProvider.GetService(serviceType) : ServiceProvider.GetKeyedService(serviceType, serviceKey);
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

    private ClaimsPrincipal GetClaimsPrincipal() => User;

    public async Task AddTransportAsync(IChannelTransport transport, CancellationToken cancellationToken = default)
    {
        var scoped = new ScopedChannelTransport(transport, ServiceProvider, id => RemoveTransport(id, alreadyDisposed: true));
        scoped.OnMessageReceived(OnMessageReceivedCore);
        scoped.OnAudioReceived(OnAudioReceivedCore);
        scoped.OnDisconnected(id => RemoveTransport(id));
        await scoped.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var subscription = _outboundAudio.Subscribe();
        var pumpTask = scoped.Metadata.SupportsAudio
            ? Task.Run(() => PumpAudioToTransportAsync(scoped, subscription, cancellationToken), cancellationToken)
            : Task.CompletedTask;

        var binding = new TransportBinding(scoped, subscription, pumpTask);
        _transports[scoped.ChannelId] = binding;
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
            SupportsAudio = bindings.Any(b => b.Transport.Metadata.SupportsAudio),
            SupportsMessaging = bindings.Any(b => b.Transport.Metadata.SupportsMessaging),
            SupportsVideo = bindings.Any(b => b.Transport.Metadata.SupportsVideo),
            SupportsScreenShare = bindings.Any(b => b.Transport.Metadata.SupportsScreenShare)
        };
    }

    private static async Task PumpAudioToTransportAsync(
        ScopedChannelTransport transport,
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
                    await transport.SendAudioAsync(chunk, cancellationToken).ConfigureAwait(false);
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
        ScopedChannelTransport Transport,
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

    private sealed class ClaimsPrincipalServiceProvider : IServiceProvider
    {
        private readonly IServiceProvider _inner;
        private readonly Func<ClaimsPrincipal> _principalAccessor;
        public ClaimsPrincipalServiceProvider(IServiceProvider inner, Func<ClaimsPrincipal> principalAccessor)
        {
            _inner = inner;
            _principalAccessor = principalAccessor;
        }
        public object? GetKeyedService(Type serviceType, object? serviceKey) => _inner.GetKeyedService(serviceType, serviceKey);
        public object GetRequiredKeyedService(Type serviceType, object? serviceKey) => _inner.GetRequiredKeyedService(serviceType, serviceKey);
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ClaimsPrincipal)) return _principalAccessor();

            return _inner.GetService(serviceType);
        }
    }
}

