using System.Security.Claims;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.Extensions.LiveVoice.Media.Audio;
using Agents.AI.Extensions.LiveVoice.Media.Messaging;
using Agents.AI.Extensions.LiveVoice.Media.Signaling;
using Agents.AI.Extensions.LiveVoice.Media.Transcription;
using Agents.AI.RealtimeVoice.Azure.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.RealtimeVoice.Azure.Transports;


public interface IScopedChannelTransport : IChannelTransport
{
    string? UserIdentifier { get; set; }
    ClaimsPrincipal User { get; }
    object? GetService(Type serviceType, object? serviceKey = null);
}

/// <summary>
/// Transparent scoped wrapper around any <see cref="IChannelTransport"/>.
/// Implements all media interfaces and delegates to the inner transport when it
/// supports the corresponding capability. Consumers should verify
/// <see cref="IChannelTransport.Metadata"/> for authoritative capability checks.
/// </summary>
public sealed class ScopedChannelTransport : IScopedChannelTransport, IAudioConsumer, IAudioProducer, IMessageConsumer, IMessageProducer
{
    private readonly IChannelTransport _innerTransport;
    private readonly IServiceProvider _serviceProvider;
    private readonly Func<string, Task>? _onDisposed;
    private ClaimsPrincipal? _user;
    private int _disposed;

    public ScopedChannelTransport(IChannelTransport transport, IServiceProvider serviceProvider, Func<string, Task>? onDisposed = null)
    {
        _innerTransport = transport;
        _serviceProvider = serviceProvider;
        _onDisposed = onDisposed;
    }

    public bool IsConnected => _disposed == 0 && _innerTransport.IsConnected;
    public string? UserIdentifier { get; set; }

    public ClaimsPrincipal User
    {
        get
        {
            if (_user is null)
            {
                _user = new ClaimsPrincipal();
            }
            return _user;
        }
    }

    public string ChannelId => _innerTransport.ChannelId;
    public ParticipantTransportMetadata Metadata => _innerTransport.Metadata;

    public Task ConnectAsync(CancellationToken cancellationToken = default) => _innerTransport.ConnectAsync(cancellationToken);
    public void SetOnDisconnected(Func<string, Task> handler) => _innerTransport.SetOnDisconnected(handler);

    // --- Media interface delegation ---

    public Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
        => _innerTransport is IAudioConsumer p ? p.SendAudioAsync(audioData, cancellationToken) : Task.CompletedTask;

    public void SetOnAudioReceivedCallback(Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> handler)
    {
        if (_innerTransport is IAudioProducer c)
        {
            c.SetOnAudioReceivedCallback(handler);
        }
    }

    public Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default)
        => _innerTransport is IMessageConsumer p ? p.SendMessageAsync(message, cancellationToken) : Task.CompletedTask;

    public void SetOnMessageReceivedCallback(Func<string, MessageUpdate, CancellationToken, Task> handler)
    {
        if (_innerTransport is IMessageProducer c)
        {
            c.SetOnMessageReceivedCallback(handler);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _innerTransport.DisposeAsync();
        }
        finally
        {
            if (_onDisposed is not null)
            {
                try { await _onDisposed(ChannelId); } catch { }
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceKey is null
            ? _serviceProvider.GetService(serviceType)
            : _serviceProvider.GetKeyedService(serviceType, serviceKey);
    }
}
