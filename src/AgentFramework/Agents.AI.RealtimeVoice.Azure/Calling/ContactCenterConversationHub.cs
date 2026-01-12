using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Agents.AI.RealtimeVoice.Azure.Calling.ConversationSessionActivitySource;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

/// <summary>
/// Singleton service that manages multiple conversation sessions across different scopes.
/// Implements a layered scope hierarchy similar to SignalR:
/// - Hub (singleton) → creates scoped sessions
/// - Session (scoped) → manages participants and transports
/// - Participants and transports can be added from different request scopes
/// </summary>
public sealed class ContactCenterConversationHub : IHostedService
{
    private const string MeterName = "Agents.AI.RealtimeVoice.Azure.Calling";
    private const string ActivitySourceName = "Agents.AI.RealtimeVoice.Azure.Calling";

    private readonly ConcurrentDictionary<string, ContactCenterConversationSession> _activeSessions = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ContactCenterConversationHub> _logger;
    private readonly Timer _cleanupTimer;
    // Telemetry
    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;
    private readonly Counter<int> _sessionCreatedCounter;
    private readonly Counter<int> _sessionClosedCounter;
    private readonly UpDownCounter<int> _activeSessionsGauge;
    private readonly Histogram<double> _sessionDurationHistogram;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IContactCenterConversationSessionActivator _sessionActivator;
    private readonly ILiveCallRegistry? _liveCallRegistry;


    public ContactCenterConversationHub(
        IServiceScopeFactory scopeFactory,
        IContactCenterConversationSessionActivator sessionActivator,
        ILiveCallRegistry? liveCallRegistry = null,
        ILoggerFactory? loggerFactory = null)
    {
        _scopeFactory = scopeFactory;
        _sessionActivator = sessionActivator;
        _liveCallRegistry = liveCallRegistry;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<ContactCenterConversationHub>();

        // Initialize telemetry
        _activitySource = new ActivitySource(ActivitySourceName);
        _meter = new Meter(MeterName);

        _sessionCreatedCounter = _meter.CreateCounter<int>(
            HubSessionsCreatedAttributeKey,
            description: "Number of conversation sessions created");

        _sessionClosedCounter = _meter.CreateCounter<int>(
            HubSessionsClosedAttributeKey,
            description: "Number of conversation sessions closed");

        _activeSessionsGauge = _meter.CreateUpDownCounter<int>(
            HubSessionsActiveAttributeKey,
            description: "Number of currently active conversation sessions");

        _sessionDurationHistogram = _meter.CreateHistogram<double>(
            SessionDurationAttributeKey,
            unit: "s",
            description: "Duration of conversation sessions in seconds");

        // Cleanup timer for abandoned sessions
        _cleanupTimer = new Timer(CleanupAbandonedSessions, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var activity = StartActivity(HubOperations.StartHub);
        _logger.LogInformation("ConversationHub started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        using var activity = StartActivity(HubOperations.StopHub);
        _logger.LogInformation("ConversationHub stopping with {ActiveSessionCount} active sessions...", _activeSessions.Count);

        // Gracefully close all sessions
        var tasks = _activeSessions.Values.Select(s => s.CloseAsync("Hub shutdown"));
        await Task.WhenAll(tasks);

        _logger.LogInformation("ConversationHub stopped");
    }

    /// <summary>
    /// Creates or retrieves a conversation session.
    /// Each session has its own scope that lives for the session's lifetime.
    /// </summary>
    /// <param name="sessionId">Unique identifier for the session</param>
    /// <param name="options">Optional session configuration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created or existing session</returns>
    public ContactCenterConversationSession GetOrCreateSession(
        string sessionId)
    {
        using var activity = StartActivity("ConversationHub.GetOrCreateSession");
        activity?.SetTag(SessionIdAttributeKey, sessionId);

        if (_activeSessions.TryGetValue(sessionId, out var existingSession))
        {
            activity?.SetTag(HubSessionIsNewAttributeKey, false);
            _logger.LogDebug("Retrieved existing session: {SessionId}", sessionId);
            return existingSession;
        }

        // Create a dedicated scope for the session that will live for the session's lifetime
        var sessionScope = _scopeFactory.CreateScope();

        var session = _sessionActivator.Create(sessionId, sessionScope, _loggerFactory);

        _activeSessions[sessionId] = session;

        // Register with LiveCallRegistry for operator dashboard
        RegisterSessionWithLiveCallRegistry(session);

        // Update metrics
        _sessionCreatedCounter.Add(1, new KeyValuePair<string, object?>(SessionIdAttributeKey, sessionId));
        _activeSessionsGauge.Add(1);

        activity?.SetTag(HubSessionIsNewAttributeKey, true);
        activity?.SetTag(HubSessionTotalActiveAttributeKey, _activeSessions.Count);

        _logger.LogInformation(
            "Created new conversation session: {SessionId}. Total active sessions: {ActiveCount}",
            sessionId,
            _activeSessions.Count);

        return session;
    }

    /// <summary>
    /// Gets an existing session by ID
    /// </summary>
    public ContactCenterConversationSession? TryGetSession(string sessionId)
    {
        _activeSessions.TryGetValue(sessionId, out var session);
        return session;
    }

    /// <summary>
    /// Removes a session from the hub
    /// </summary>
    public async Task<bool> RemoveSessionAsync(string sessionId)
    {
        using var activity = StartActivity(HubOperations.RemoveSession);
        activity?.SetTag(SessionIdAttributeKey, sessionId);

        if (_activeSessions.TryRemove(sessionId, out var session))
        {
            var sessionDuration = (DateTimeOffset.UtcNow - session.CreatedAt).TotalSeconds;

            await session.DisposeAsync();

            // Mark session as ended in LiveCallRegistry
            _liveCallRegistry?.EndSession(sessionId, DateTimeOffset.UtcNow);

            // Update metrics
            _sessionClosedCounter.Add(1, new KeyValuePair<string, object?>(SessionIdAttributeKey, sessionId));
            _activeSessionsGauge.Add(-1);
            _sessionDurationHistogram.Record(sessionDuration, new KeyValuePair<string, object?>(SessionIdAttributeKey, sessionId));

            activity?.SetTag(HubSessionDurationAttributeKey, sessionDuration);
            activity?.SetTag(HubSessionTotalActiveAttributeKey, _activeSessions.Count);

            _logger.LogInformation(
                "Removed conversation session: {SessionId}. Duration: {Duration:F2}s. Remaining active sessions: {ActiveCount}",
                sessionId,
                sessionDuration,
                _activeSessions.Count);

            return true;
        }

        _logger.LogWarning("Attempted to remove non-existent session: {SessionId}", sessionId);
        return false;
    }

    /// <summary>
    /// Gets all active sessions
    /// </summary>
    public IReadOnlyDictionary<string, ContactCenterConversationSession> GetActiveSessions()
        => new Dictionary<string, ContactCenterConversationSession>(_activeSessions);

    private void CleanupAbandonedSessions(object? state)
    {
        using var activity = StartActivity(HubOperations.CleanupAbandonedSessions);

        var cutoffTime = DateTimeOffset.UtcNow.AddMinutes(-30);
        var abandonedSessions = _activeSessions
            .Where(kvp => !kvp.Value.IsActive)
            .Select(kvp => kvp.Key)
            .ToList();

        activity?.SetTag(HubSessionAbandonedAttributeKey, abandonedSessions.Count);
        activity?.SetTag(HubSessionCutoffTimeAttributeKey, cutoffTime);

        foreach (var sessionId in abandonedSessions)
        {
            _ = RemoveSessionAsync(sessionId);
        }

        if (abandonedSessions.Any())
        {
            _logger.LogInformation(
                "Cleaned up {Count} abandoned sessions (inactive since {CutoffTime:o})",
                abandonedSessions.Count,
                cutoffTime);
        }
    }

    private Activity? StartActivity(string shortOperationName)
    {
        if (!_activitySource.HasListeners())
        {
            return null;
        }
        string activityName = $"{HubActivityAttributeKey} {shortOperationName}";
        var activity = _activitySource.StartActivity(activityName, ActivityKind.Server);

        return activity;
    }

    private void RegisterSessionWithLiveCallRegistry(ContactCenterConversationSession session)
    {
        if (_liveCallRegistry is null)
        {
            return;
        }

        try
        {
            var summary = new LiveCallSummary
            {
                SessionId = session.SessionId,
                StartedAt = session.CreatedAt,
                Status = LiveCallStatus.Active,
                Participants = []
            };

            _liveCallRegistry.Upsert(summary);
            _logger.LogDebug("Registered session {SessionId} with LiveCallRegistry", session.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register session {SessionId} with LiveCallRegistry", session.SessionId);
        }
    }
}
