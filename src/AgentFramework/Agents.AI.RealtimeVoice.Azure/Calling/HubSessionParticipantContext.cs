using System.Security.Claims;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;
using Agents.AI.RealtimeVoice.Azure.Calling.Transports;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

public sealed class HubSessionParticipantContext : IScopedChannelTransport
{
    private readonly IServiceScope _participantScope;
    private readonly HashSet<ScopedChannelTransport> _transports = [];
    private Func<string, ReadOnlyMemory<byte>, CancellationToken, Task>? _audioHandler;
    private Func<string, MessageUpdate, CancellationToken, Task>? _messageHandler;
    private Func<string, Task>? _disconnectedHandler;
    private readonly string _participantId;
    private string? _displayName;
    private ClaimsPrincipalServiceProvider ServiceProvider => new(_participantScope.ServiceProvider, GetClaimsPrincipal);

    public HubSessionParticipantContext(IServiceScope participantScope, string participantId, string? displayName = null)
    {
        _participantScope = participantScope;
        _participantId = participantId;
        _displayName = displayName;
    }

    public string ChannelId => ParticipantId;
    public string ParticipantId => _participantId;
    public bool IsConnected { get; private set; } = false;
    public string? DisplayName { get => _displayName; set => _displayName = value; }
    public string? UserIdentifier { get; set; }
    public IReadOnlyList<IScopedChannelTransport> Transports => [.. _transports];

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        foreach (var transport in _transports)
        {
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        IsConnected = true;
    }

    public ClaimsPrincipal User => Transports.Select(t => t.User).Aggregate((current, next) =>
    {
        var combined = new ClaimsPrincipal(current);
        combined.AddIdentities(next.Identities);
        return combined;
    });

    public ParticipantTransportMetadata Metadata
    {
        get
        {
            var firstTransport = _transports.FirstOrDefault();
            if (firstTransport is null)
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

            return new ParticipantTransportMetadata
            {
                ContactId = ParticipantId,
                ChannelType = firstTransport.Metadata.ChannelType,
                RawIdentifier = ParticipantId,
                DisplayName = DisplayName ?? firstTransport.Metadata.DisplayName,
                SupportsAudio = _transports.Any(t => t.Metadata.SupportsAudio),
                SupportsMessaging = _transports.Any(t => t.Metadata.SupportsMessaging),
                SupportsVideo = _transports.Any(t => t.Metadata.SupportsVideo),
                SupportsScreenShare = _transports.Any(t => t.Metadata.SupportsScreenShare)
            };
        }
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        foreach (var transport in _transports)
        {
            if (transport.Metadata.SupportsAudio)
            {
                _ = transport.SendAudioAsync(audioData, cancellationToken);
            }
        }
    }

    public async Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default)
    {
        foreach (var transport in _transports)
        {
            if (transport.Metadata.SupportsMessaging)
            {
                _ = transport.SendMessageAsync(message, cancellationToken);
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
        try
        {
            foreach (var transport in _transports)
            {
                await transport.DisposeAsync();
            }
            _transports.Clear();
            _participantScope.Dispose();
        }
        catch (Exception) { }
        finally
        {
            if (_disconnectedHandler is not null)
            {
                try { await _disconnectedHandler(ChannelId); } catch { }
            }
        }
    }

    private Task OnAudioReceivedCore(string channelId, ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken)
    {
        if (_audioHandler is null) return Task.CompletedTask;
        return _audioHandler(ChannelId, audioData, cancellationToken);
    }

    private Task OnMessageReceivedCore(string channelId, MessageUpdate update, CancellationToken cancellationToken)
    {
        if (_messageHandler is null) return Task.CompletedTask;
        return _messageHandler(ChannelId, update, cancellationToken);
    }

    private ClaimsPrincipal GetClaimsPrincipal() => User;

    internal async Task AddTransportAsync(IChannelTransport transport, CancellationToken cancellationToken = default)
    {
        var scoped = new ScopedChannelTransport(transport, ServiceProvider, async id => await RemoveTransport(id, alreadyDisposed: true));
        scoped.OnMessageReceived(OnMessageReceivedCore);
        scoped.OnAudioReceived(OnAudioReceivedCore);
        scoped.OnDisconnected(async id => await RemoveTransport(id));
        await scoped.ConnectAsync(cancellationToken).ConfigureAwait(false);
        _transports.Add(scoped);
    }

    internal async Task<bool> RemoveTransport(string transportId, bool alreadyDisposed = false)
    {
        var toRemove = _transports.FirstOrDefault(t => t.ChannelId == transportId);
        if (toRemove is null) return false;
        _transports.Remove(toRemove);
        if (!alreadyDisposed)
        {
            await toRemove.DisposeAsync();
        }
        return true;
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

