using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.RealtimeVoice.Azure.Calling.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Agents.AI.RealtimeVoice.Azure.Calling.ConversationSessionActivitySource;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

public class TransferMetadata
{
    public string Reason { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? OriginalCallId { get; set; }
    public string? AgentParticipantId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

public sealed class ContactCenterConversationSession : IAsyncDisposable
{
    private readonly ILogger<ContactCenterConversationSession> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceScope _sessionScope;
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly SemaphoreSlim _initSemaphore = new(1, 1);
    private readonly SemaphoreSlim _transferLock = new(1, 1);

    private readonly HubSessionContext _hubSessionContext; 

    private readonly ConcurrentDictionary<string, HubSessionParticipantContext> _participantContexts = new();
    private TransferMetadata? _pendingTransfer;

    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;
    private readonly TotalAudioPacketsRoutedCounter _audioPacketsCounter;
    private readonly TotalAudioBytesRoutedCounter _audioBytesCounter;
    private readonly UpDownCounter<int> _participantsGauge;
    private readonly UpDownCounter<int> _channelsGauge;
    private readonly Histogram<double> _audioLatencyHist;
    private readonly Histogram<double> _messageLatencyHist;

    public string SessionId => _hubSessionContext.SessionId;
    public DateTimeOffset CreatedAt { get; }
    public bool IsActive => !_sessionCts.IsCancellationRequested;
    public SessionState State { get; private set; } = SessionState.Created;

    public IReadOnlyDictionary<string, HubSessionParticipantContext> ParticipantContexts => _participantContexts;
    public HubSessionContext HubSessionContext => _hubSessionContext;

    public ContactCenterConversationSession(
        IServiceScope sessionScope,
        HubSessionContext hubSessionContext,
        ILoggerFactory? loggerFactory = null)
    {
        _sessionScope = sessionScope ?? throw new ArgumentNullException(nameof(sessionScope));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<ContactCenterConversationSession>();
        _hubSessionContext = hubSessionContext;

        CreatedAt = DateTimeOffset.UtcNow;
        _activitySource = ConversationSessionActivitySource.ActivitySource;
        _meter = new(MeterName);

        _audioPacketsCounter = CreateAudioPacketsBetweenChannels(_meter);
        _audioBytesCounter = CreateAudioBytesBetweenChannels(_meter);

        _participantsGauge = _meter.CreateUpDownCounter<int>(SessionParticipantsActiveAttributeKey);
        _channelsGauge = _meter.CreateUpDownCounter<int>(SessionChannelsActiveAttributeKey);

        _audioLatencyHist = _meter.CreateHistogram<double>(SessionAudioRoutingLatencyAttributeKey, unit: "ms");
        _messageLatencyHist = _meter.CreateHistogram<double>(SessionMessageRoutingLatencyAttributeKey, unit: "ms");
    }

    #region Participant Management
    /// <summary>
    /// Gets or creates a participant context by id. Optionally sets display name if newly created.
    /// </summary>
    public HubSessionParticipantContext GetOrAddParticipant(string participantId, string? displayName = null)
    {
        ThrowIfDisposed();

        HubSessionParticipantContext Factory(string id)
        {
            var participantScope = _sessionScope.ServiceProvider.CreateAsyncScope();
            var participantContext = new HubSessionParticipantContext(participantScope, id, displayName);
            HookInboundHandlers(participantContext);
            _participantsGauge.Add(1);
            return participantContext;
        }

        return _participantContexts.GetOrAdd(participantId, Factory);
    }


    public async Task<bool> RemoveParticipantAsync(string participantId, CancellationToken _ = default)
    {
        if (!_participantContexts.TryRemove(participantId, out var context))
        {
            return false;
        }
        await context.DisposeAsync();
        _participantsGauge.Add(-1);
        _logger.LogInformation("Participant {ParticipantId} removed.", participantId);
        return true;
    }

    public HubSessionParticipantContext? GetParticipantContext(string participantId)
    {
        _participantContexts.TryGetValue(participantId, out var ctx);
        return ctx;
    }
    #endregion

    #region Transport Management
    public async Task<bool> AddTransportToParticipant(string participantId, IChannelTransport transport)
    {
        ThrowIfDisposed();
        var context = GetOrAddParticipant(participantId);
        await context.AddTransportAsync(transport, _sessionCts.Token).ConfigureAwait(false);
        _channelsGauge.Add(1);
        _logger.LogInformation("Transport {ChannelId} added to participant {ParticipantId}", transport.ChannelId, participantId);
        return true;
    }

    public async Task<bool> AddTransportToParticipant(string participantId, Func<IServiceProvider, Task<IChannelTransport>> transportFactory)
    {
        ThrowIfDisposed();
        var context = GetOrAddParticipant(participantId);
        var transport = await transportFactory(_sessionScope.ServiceProvider).ConfigureAwait(false);
        await context.AddTransportAsync(transport, _sessionCts.Token).ConfigureAwait(false);
        _channelsGauge.Add(1);
        _logger.LogInformation("Transport {ChannelId} added to participant {ParticipantId}", transport.ChannelId, participantId);
        return true;
    }

    public async Task<bool> RemoveTransportFromParticipant(string participantId, string channelId)
    {
        if (!_participantContexts.TryGetValue(participantId, out var context))
        {
            return false;
        }
        var removed = await context.RemoveTransport(channelId, false).ConfigureAwait(false);
        _channelsGauge.Add(-1);
        _logger.LogInformation("Transport {ChannelId} removed from participant {ParticipantId}", channelId, participantId);
        return removed;
    }


    private void HookInboundHandlers(HubSessionParticipantContext participantContext)
    {
        participantContext.OnAudioReceived(OnAudioAsync);

        participantContext.OnMessageReceived(OnMessageAsync);
    }

    private async Task OnMessageAsync(string sourceId, MessageUpdate message, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        var tasks = new List<Task>();
        var targetCount = 0;
        foreach (var pc in _participantContexts.Values)
        {
            if (pc.ChannelId != sourceId)
            {
                tasks.Add(pc.SendMessageAsync(message, ct));
                targetCount++;
            }
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _messageLatencyHist.Record(elapsed, new KeyValuePair<string, object?>(SessionTargetChannelCountAttributeKey, targetCount));
    }
    private async Task OnAudioAsync(string sourceId, ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        var tasks = new List<Task>();
        var targetCount = 0;
        foreach (var pc in _participantContexts.Values)
        {
            if (pc.ChannelId != sourceId)
            {
                tasks.Add(pc.SendAudioAsync(frame, ct));
                targetCount++;
            }
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _audioPacketsCounter.Add(targetCount, SessionId, sourceId);
        _audioBytesCounter.Add(frame.Length * targetCount, SessionId, sourceId);
        _audioLatencyHist.Record(elapsed, new KeyValuePair<string, object?>(SessionTargetChannelCountAttributeKey, targetCount));
    }
    #endregion

    #region Transfer / Context
    public async Task SetTransferMetadataAsync(TransferMetadata metadata, CancellationToken ct = default)
    {
        await _transferLock.WaitAsync(ct);
        try
        {
            _pendingTransfer = metadata;
        }
        finally
        {
            _transferLock.Release();
        }
    }

    public async Task<TransferMetadata?> GetAndClearTransferMetadataAsync(CancellationToken ct = default)
    {
        await _transferLock.WaitAsync(ct);
        try
        {
            var tmp = _pendingTransfer;
            _pendingTransfer = null;
            return tmp;
        }
        finally
        {
            _transferLock.Release();
        }
    }
    #endregion

    #region Session Management / Disposal
    public async Task CloseAsync(string? reason = null)
    {
        if (State is SessionState.Closed or SessionState.Closing)
        {
            return;
        }
        State = SessionState.Closing;
        _logger.LogInformation("Closing session {SessionId}. Reason: {Reason}", SessionId, reason ?? "None");
        var tasks = _participantContexts.Keys.Select(pid => RemoveParticipantAsync(pid));
        await Task.WhenAll(tasks);
        State = SessionState.Closed;
    }

    private void ThrowIfDisposed()
    {
        if (_sessionCts.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(ContactCenterConversationSession), $"Session {SessionId} disposed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_sessionCts.IsCancellationRequested)
        {
            return;
        }
        using var activity = _activitySource.StartActivity("TransportSession.Dispose");
        activity?.SetTag(SessionActivityAttributeKey, SessionId);
        await CloseAsync("Disposing");
        await _sessionCts.CancelAsync();

        _initSemaphore.Dispose();
        _sessionCts.Dispose();
        _meter.Dispose();
        _sessionScope.Dispose();
        _logger.LogInformation("Transport session {SessionId} disposed", SessionId);
    }
    #endregion

    public enum SessionState
    {
        Created,
        Initializing,
        Active,
        Closing,
        Closed,
        Failed
    }
}
