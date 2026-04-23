using System.Collections.Concurrent;
using System.Diagnostics;
using Agents.AI.Extensions.Helpers;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.LiveVoice.Media.Analysis;
using Agents.AI.RealtimeVoice.Azure.Calling.Routing;
using Agents.AI.RealtimeVoice.Azure.Monitoring;
using Agents.AI.RealtimeVoice.Azure.Transports;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Agents.AI.RealtimeVoice.Azure.Monitoring.ConversationSessionActivitySource;

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

    private readonly HubSessionContext _hubSessionContext;
    private readonly HubSessionEventBus _eventBus = new();
    private readonly ISessionRouter _router;
    private readonly SessionTelemetry _telemetry;
    private readonly ConversationContext _conversationContext;
    private readonly ILiveCallRegistry? _liveCallRegistry;
    private readonly SessionContextSubscription _contextProjectionSubscription;
    private readonly Task _contextProjectionTask;

    private readonly ConcurrentDictionary<string, HubSessionParticipant> _participants = new();

    // Per-session source counters (session-scoped Meter lives here because the counter names contain sessionId)
    private readonly TotalAudioPacketsRoutedCounter _audioPacketsCounter;
    private readonly TotalAudioBytesRoutedCounter _audioBytesCounter;

    private SessionState _state = SessionState.Created;

    public string SessionId => _hubSessionContext.SessionId;
    public DateTimeOffset CreatedAt { get; }
    public bool IsActive => !_sessionCts.IsCancellationRequested;
    public SessionState State => _state;

    public IReadOnlyDictionary<string, HubSessionParticipant> ParticipantContexts => _participants;
    public HubSessionContext HubSessionContext => _hubSessionContext;

    /// <summary>
    /// Session-scoped pub/sub for structured context events.
    /// All channels publish context here; AI agents subscribe to build
    /// unified awareness across all interaction modalities.
    /// <para>
    /// Audio frames never flow through this bus — they stay on their dedicated
    /// <see cref="RawMediaStreamChannel"/> path.
    /// </para>
    /// </summary>
    public HubSessionEventBus SessionEventBus => _eventBus;

    public ContactCenterConversationSession(
        IServiceScope sessionScope,
        HubSessionContext hubSessionContext,
        ISessionRouter router,
        SessionTelemetry telemetry,
        ILoggerFactory? loggerFactory = null)
    {
        _sessionScope = sessionScope ?? throw new ArgumentNullException(nameof(sessionScope));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<ContactCenterConversationSession>();
        _hubSessionContext = hubSessionContext;
        _router = router;
        _telemetry = telemetry;
        _conversationContext = hubSessionContext.ConversationContext;
        _liveCallRegistry = hubSessionContext.GetService<ILiveCallRegistry>();

        CreatedAt = DateTimeOffset.UtcNow;

        _audioPacketsCounter = CreateAudioPacketsBetweenChannels(_telemetry.SessionMeter);
        _audioBytesCounter = CreateAudioBytesBetweenChannels(_telemetry.SessionMeter);
        _contextProjectionSubscription = _eventBus.Subscribe(static contextEvent =>
            contextEvent.Kind is HubSessionEventKind.ChatMessage
                or HubSessionEventKind.Transcript
                or HubSessionEventKind.AgentInsight
                or HubSessionEventKind.TransferInitiated
                or HubSessionEventKind.TransferCompleted);
        _contextProjectionTask = Task.Run(
            () => ProjectConversationContextAsync(_contextProjectionSubscription, _sessionCts.Token),
            _sessionCts.Token);

        TransitionTo(SessionState.Active);
    }

    #region State Machine
    private void TransitionTo(SessionState target)
    {
        var valid = (_state, target) switch
        {
            (SessionState.Created, SessionState.Active) => true,
            (SessionState.Active, SessionState.Transferring) => true,
            (SessionState.Active, SessionState.OnHold) => true,
            (SessionState.Active, SessionState.Closing) => true,
            (SessionState.Transferring, SessionState.Active) => true,
            (SessionState.Transferring, SessionState.Closing) => true,
            (SessionState.OnHold, SessionState.Active) => true,
            (SessionState.OnHold, SessionState.Closing) => true,
            (SessionState.Closing, SessionState.Closed) => true,
            _ => false
        };

        if (!valid)
        {
            throw new InvalidOperationException(
                $"Invalid session state transition: {_state} → {target}");
        }

        _state = target;
    }

    private void ThrowIfNotActive()
    {
        if (_sessionCts.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(ContactCenterConversationSession), $"Session {SessionId} disposed");
        }

        if (_state is SessionState.Closing or SessionState.Closed)
        {
            throw new InvalidOperationException($"Session {SessionId} is in state {_state} and cannot accept new operations");
        }
    }
    #endregion

    #region Participant Management
    /// <summary>
    /// Gets or creates a participant context by id. Optionally sets display name if newly created.
    /// </summary>
    public Task<HubSessionParticipant> GetOrAddParticipantAsync(string participantId, string? displayName = null, CancellationToken cancellationToken = default)
    {
        ThrowIfNotActive();

        async Task<HubSessionParticipant> Factory(string id)
        {
            var participantScope = _sessionScope.ServiceProvider.CreateAsyncScope();
            var participantContext = new HubSessionParticipant(participantScope, id, displayName);
            HookInboundHandlers(participantContext);
            _telemetry.RecordParticipantAdded();

            await _eventBus.PublishAsync(new SessionContextEvent
            {
                EventId = $"session_join_{id}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                Kind = HubSessionEventKind.ParticipantJoined,
                SourceParticipantId = id,
                Payload = new { ParticipantId = id, DisplayName = displayName }
            }, cancellationToken);

            return participantContext;
        }

        return _participants.GetOrAddAsync(participantId, Factory);
    }


    public async Task<bool> RemoveParticipantAsync(string participantId, CancellationToken cancellationToken = default)
    {
        if (!_participants.TryRemove(participantId, out var participant))
        {
            return false;
        }
        await participant.DisposeAsync();
        _telemetry.RecordParticipantRemoved();

        await _eventBus.PublishAsync(new SessionContextEvent
        {
            EventId = $"session_leave_{participantId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            Kind = HubSessionEventKind.ParticipantLeft,
            SourceParticipantId = participantId,
            Payload = new { ParticipantId = participantId }
        }, cancellationToken);

        _logger.LogInformation("Participant {ParticipantId} removed.", participantId);
        return true;
    }

    public HubSessionParticipant? GetParticipantContext(string participantId)
    {
        _participants.TryGetValue(participantId, out var ctx);
        return ctx;
    }
    #endregion

    #region Transport Management
    public async Task<bool> AddTransportToParticipantAsync(string participantId, IChannelTransport transport)
    {
        ThrowIfNotActive();
        var context = await GetOrAddParticipantAsync(participantId, cancellationToken: _sessionCts.Token);
        await context.AddTransportAsync(transport, _sessionCts.Token).ConfigureAwait(false);
        _telemetry.RecordChannelAdded();
        _logger.LogInformation("Transport {ChannelId} added to participant {ParticipantId}", transport.ChannelId, participantId);
        return true;
    }

    public async Task<bool> AddTransportToParticipantAsync(string participantId, Func<IServiceProvider, Task<IChannelTransport>> transportFactory)
    {
        ThrowIfNotActive();
        var context = await GetOrAddParticipantAsync(participantId, cancellationToken: _sessionCts.Token);
        var transport = await transportFactory(_sessionScope.ServiceProvider).ConfigureAwait(false);
        await context.AddTransportAsync(transport, _sessionCts.Token).ConfigureAwait(false);
        _telemetry.RecordChannelAdded();
        _logger.LogInformation("Transport {ChannelId} added to participant {ParticipantId}", transport.ChannelId, participantId);
        return true;
    }

    public async Task<bool> RemoveTransportFromParticipantAsync(string participantId, string channelId)
    {
        if (!_participants.TryGetValue(participantId, out var context))
        {
            return false;
        }
        var removed = await context.RemoveTransport(channelId, false).ConfigureAwait(false);
        _telemetry.RecordChannelRemoved();
        _logger.LogInformation("Transport {ChannelId} removed from participant {ParticipantId}", channelId, participantId);
        return removed;
    }


    private void HookInboundHandlers(HubSessionParticipant participantContext)
    {
        participantContext.OnAudioReceived(OnAudioAsync);

        participantContext.OnMessageReceived(OnMessageAsync);
    }

    private async Task OnMessageAsync(string sourceId, MessageUpdate message, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();

        // Delegate routing to the router strategy
        var targetCount = await _router.RouteMessageAsync(sourceId, message, _participants, ct).ConfigureAwait(false);

        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _telemetry.RecordMessageRouted(targetCount, elapsed);

        // Publish to context bus AFTER routing completes — non-blocking TryWrite, zero impact on delivery
        await _eventBus.PublishAsync(new SessionContextEvent
        {
            EventId = message.MessageId ?? Guid.NewGuid().ToString("N"),
            Kind = HubSessionEventKind.ChatMessage,
            SourceParticipantId = sourceId,
            TargetParticipantId = message.TargetParticipantId,
            Payload = message
        }, ct).ConfigureAwait(false);
    }

    private async Task OnAudioAsync(string sourceId, ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();

        // Delegate routing to the router strategy
        await _router.RouteAudioAsync(sourceId, frame, _participants, ct).ConfigureAwait(false);

        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var targetCount = _participants.Count - 1; // approximate: all except source
        _telemetry.RecordAudioRouted(SessionId, sourceId, targetCount, frame.Length, elapsed, _audioPacketsCounter, _audioBytesCounter);
    }
    #endregion

    #region Transfer / Context
    /// <summary>
    /// Initiates a transfer by publishing a <see cref="HubSessionEventKind.TransferInitiated"/>
    /// event to the session's event bus and transitioning to the Transferring state.
    /// </summary>
    public async Task InitiateTransferAsync(TransferMetadata metadata, CancellationToken ct = default)
    {
        TransitionTo(SessionState.Transferring);

        await _eventBus.PublishAsync(new SessionContextEvent
        {
            EventId = $"transfer_init_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            Kind = HubSessionEventKind.TransferInitiated,
            SourceParticipantId = metadata.AgentParticipantId ?? SessionId,
            Payload = metadata
        }, ct).ConfigureAwait(false);

        _logger.LogInformation("Transfer initiated for session {SessionId}. Reason: {Reason}", SessionId, metadata.Reason);
    }

    /// <summary>
    /// Completes a transfer by publishing a <see cref="HubSessionEventKind.TransferCompleted"/>
    /// event and transitioning back to the Active state.
    /// </summary>
    public async Task CompleteTransferAsync(TransferMetadata metadata, CancellationToken ct = default)
    {
        TransitionTo(SessionState.Active);

        await _eventBus.PublishAsync(new SessionContextEvent
        {
            EventId = $"transfer_done_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            Kind = HubSessionEventKind.TransferCompleted,
            SourceParticipantId = metadata.AgentParticipantId ?? SessionId,
            Payload = metadata
        }, ct).ConfigureAwait(false);

        _logger.LogInformation("Transfer completed for session {SessionId}.", SessionId);
    }
    #endregion

    #region Session Management / Disposal
    public async Task CloseAsync(string? reason = null)
    {
        if (_state is SessionState.Closed or SessionState.Closing)
        {
            return;
        }
        TransitionTo(SessionState.Closing);
        _logger.LogInformation("Closing session {SessionId}. Reason: {Reason}", SessionId, reason ?? "None");
        var tasks = _participants.Keys.Select(pid => RemoveParticipantAsync(pid));
        await Task.WhenAll(tasks);
        TransitionTo(SessionState.Closed);
    }

    public async ValueTask DisposeAsync()
    {
        if (_sessionCts.IsCancellationRequested)
        {
            return;
        }
        using var activity = _telemetry.StartSessionActivity("TransportSession.Dispose", SessionId);
        activity?.SetTag(SessionActivityAttributeKey, SessionId);
        await CloseAsync("Disposing");
        await _sessionCts.CancelAsync();

        await _contextProjectionSubscription.DisposeAsync();

        try
        {
            await _contextProjectionTask;
        }
        catch (OperationCanceledException)
        {
        }

        await _eventBus.DisposeAsync();

        _initSemaphore.Dispose();
        _sessionCts.Dispose();
        _sessionScope.Dispose();
        _logger.LogInformation("Transport session {SessionId} disposed", SessionId);
    }
    #endregion

    public enum SessionState
    {
        Created,
        Active,
        Transferring,
        OnHold,
        Closing,
        Closed,
        Failed
    }

    private async Task ProjectConversationContextAsync(
        SessionContextSubscription subscription,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var contextEvent in subscription.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _conversationContext.TotalDuration = DateTimeOffset.UtcNow - CreatedAt;

                switch (contextEvent.Payload)
                {
                    case MessageUpdate message:
                        ProjectMessage(message, contextEvent.SourceParticipantId);
                        break;
                    case string transcript:
                        ProjectTranscript(transcript, contextEvent.SourceParticipantId);
                        break;
                    case ConversationSignalAnalysis analysis:
                        ProjectAnalysis(analysis);
                        break;
                    case TransferMetadata transfer when contextEvent.Kind is HubSessionEventKind.TransferInitiated:
                        _conversationContext.ActionsTaken.Add($"Transfer initiated: {transfer.Reason}");
                        break;
                    case TransferMetadata transfer when contextEvent.Kind is HubSessionEventKind.TransferCompleted:
                        _conversationContext.ActionsTaken.Add($"Transfer completed: {transfer.Reason}");
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ProjectAnalysis(ConversationSignalAnalysis analysis)
    {
        if (analysis.TextSentiment is double textSentiment)
        {
            _conversationContext.RunningTextSentiment = BlendScore(_conversationContext.RunningTextSentiment, textSentiment);
        }

        if (analysis.AudioEmotion is { } audioEmotion)
        {
            _conversationContext.RunningAudioEmotion = BlendScore(_conversationContext.RunningAudioEmotion, audioEmotion.ValenceScore);
            _conversationContext.FrustrationDetected = _conversationContext.FrustrationDetected
                || audioEmotion.Label is "frustrated" or "angry";
        }

        if (analysis.StressLevel is >= 0.7 || analysis.IsDivergent)
        {
            _conversationContext.FrustrationDetected = true;
            _conversationContext.EscalationSignalCount++;
        }

        _liveCallRegistry?.UpdateHealth(SessionId, call =>
        {
            call.CustomerSentiment = _conversationContext.RunningTextSentiment;
            call.EscalationRiskScore = Math.Clamp(
                Math.Max(
                    Math.Max(analysis.Divergence ?? 0, analysis.StressLevel ?? 0),
                    _conversationContext.FrustrationDetected ? 0.8 : 0.0),
                0.0,
                1.0);
        });
    }

    private void ProjectMessage(MessageUpdate message, string sourceParticipantId)
    {
        if (TryExtractText(message.Contents) is not { } text)
        {
            return;
        }

        _conversationContext.TurnCount++;
        _conversationContext.ConversationSummary = SummarizeText(text);

        if (string.Equals(sourceParticipantId, _conversationContext.CallerId, StringComparison.OrdinalIgnoreCase))
        {
            var utteranceLengthSec = EstimateUtteranceLengthSeconds(text);
            _conversationContext.AvgUserUtteranceLengthSec = _conversationContext.AvgUserUtteranceLengthSec <= 0
                ? utteranceLengthSec
                : (_conversationContext.AvgUserUtteranceLengthSec + utteranceLengthSec) / 2;
        }

        _liveCallRegistry?.UpdateHealth(SessionId, call =>
        {
            call.LatestUtteranceSummary = _conversationContext.ConversationSummary;
            call.ActiveTasks = BuildActiveTasks();
        });
    }

    private void ProjectTranscript(string transcript, string sourceParticipantId)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return;
        }

        ProjectMessage(
            new MessageUpdate
            {
                Contents = [new TextContent(transcript)],
                CreatedAt = DateTimeOffset.UtcNow,
                SenderParticipantId = sourceParticipantId
            },
            sourceParticipantId);
    }

    private IReadOnlyList<string> BuildActiveTasks()
    {
        var activeTasks = new List<string>();

        if (!string.IsNullOrWhiteSpace(_conversationContext.PrimaryIntent))
        {
            activeTasks.Add(_conversationContext.PrimaryIntent);
        }

        activeTasks.AddRange(_conversationContext.SecondaryIntents.Where(static intent => !string.IsNullOrWhiteSpace(intent)));

        return activeTasks.Count > 0
            ? activeTasks
            : [];
    }

    private static double BlendScore(double current, double next)
    {
        return current == 0
            ? next
            : ((current * 0.4) + (next * 0.6));
    }

    private static double EstimateUtteranceLengthSeconds(string text)
    {
        return Math.Clamp(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 2.5, 0.5, 30.0);
    }

    private static string? TryExtractText(IEnumerable<AIContent> contents)
    {
        return contents.OfType<TextContent>().FirstOrDefault(static content => !string.IsNullOrWhiteSpace(content.Text))?.Text;
    }

    private static string SummarizeText(string text)
    {
        const int maxLength = 160;

        return text.Length <= maxLength
            ? text
            : text[..(maxLength - 3)] + "...";
    }
}
