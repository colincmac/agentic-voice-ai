using System.Security.Claims;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Transports;

public static class ScopedChannelTransportExtensions
{
    public static T? GetService<T>(this IScopedChannelTransport transport, object? serviceKey = null)
        where T : class
    {
        return transport.GetService(typeof(T), serviceKey) as T;
    }
}

public interface IScopedChannelTransport : IChannelTransport
{
    string? UserIdentifier { get; set; }
    ClaimsPrincipal User { get; }
    object? GetService(Type serviceType, object? serviceKey = null);
}

public sealed class ScopedChannelTransport(IChannelTransport transport, IServiceProvider serviceProvider) : IScopedChannelTransport
{
    private readonly IChannelTransport _innerTransport = transport;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly Func<string, Task>? _onDisposed;
    private ClaimsPrincipal? _user;
    private volatile bool _isDisposed;

    public ScopedChannelTransport(IChannelTransport transport, IServiceProvider serviceProvider, Func<string, Task>? onDisposed = null) : this(transport, serviceProvider)
    {
        _onDisposed = onDisposed;
        // Propagate disconnection from inner transport so parent can remove this scoped wrapper
        _innerTransport.OnDisconnected(async id =>
        {
            if (!_isDisposed && _onDisposed is not null)
            {
                await _onDisposed(id);
            }
        }); 
    }

    public bool IsConnected => _innerTransport.IsConnected;
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
    public Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default) => _innerTransport.SendAudioAsync(audioData, cancellationToken);
    public Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default) => _innerTransport.SendMessageAsync(message, cancellationToken);
    public void OnAudioReceived(Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> handler) => _innerTransport.OnAudioReceived(handler);
    public void OnMessageReceived(Func<string, MessageUpdate, CancellationToken, Task> handler) => _innerTransport.OnMessageReceived(handler);
    public void OnDisconnected(Func<string, Task> handler) => _innerTransport.OnDisconnected(handler);

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        try
        {
            await _innerTransport.DisposeAsync();
        }
        finally
        {
            if (_onDisposed is not null)
            {
                await _onDisposed(ChannelId);
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
