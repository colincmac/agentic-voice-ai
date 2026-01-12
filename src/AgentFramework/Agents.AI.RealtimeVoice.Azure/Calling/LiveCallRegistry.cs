using System.Collections.Concurrent;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

/// <summary>
/// In-memory registry for tracking active calls for the operator dashboard.
/// Thread-safe and suitable for single-instance deployments.
/// </summary>
public sealed class LiveCallRegistry : ILiveCallRegistry
{
    private readonly ConcurrentDictionary<string, LiveCallSummary> _calls = new();
    private readonly ILogger<LiveCallRegistry> _logger;
    private readonly object _eventLock = new();

    public event EventHandler<LiveCallSummary>? CallStarted;
    public event EventHandler<LiveCallSummary>? CallEnded;
    public event EventHandler<LiveCallSummary>? CallHealthUpdated;

    public LiveCallRegistry(ILogger<LiveCallRegistry>? logger = null)
    {
        _logger = logger ?? NullLogger<LiveCallRegistry>.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<LiveCallSummary> GetActiveCalls()
    {
        return _calls.Values
            .Where(c => c.Status != LiveCallStatus.Ended && c.Status != LiveCallStatus.Failed)
            .Select(c => c.Clone())
            .ToList();
    }

    /// <inheritdoc />
    public LiveCallSummary? GetBySessionId(string sessionId)
    {
        if (_calls.TryGetValue(sessionId, out var summary))
        {
            return summary.Clone();
        }

        return null;
    }

    /// <inheritdoc />
    public void Upsert(LiveCallSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        bool isNew = false;
        _calls.AddOrUpdate(
            summary.SessionId,
            // Add factory - called only when key doesn't exist
            _ =>
            {
                isNew = true;
                return summary;
            },
            // Update factory - called when key exists
            (_, existing) => summary);

        _logger.LogDebug(
            "Upserted call summary for session {SessionId}. IsNew: {IsNew}, Status: {Status}",
            summary.SessionId,
            isNew,
            summary.Status);

        if (isNew)
        {
            OnCallStarted(summary.Clone());
        }
    }

    /// <inheritdoc />
    public LiveCallSummary? EndSession(string sessionId, DateTimeOffset endedAt)
    {
        if (!_calls.TryGetValue(sessionId, out var summary))
        {
            _logger.LogWarning("Attempted to end non-existent session: {SessionId}", sessionId);
            return null;
        }

        summary.EndedAt = endedAt;
        summary.Status = LiveCallStatus.Ended;

        _logger.LogInformation(
            "Session {SessionId} ended at {EndedAt}. Duration: {Duration}",
            sessionId,
            endedAt,
            summary.Duration);

        OnCallEnded(summary.Clone());

        return summary.Clone();
    }

    /// <inheritdoc />
    public bool Remove(string sessionId)
    {
        bool removed = _calls.TryRemove(sessionId, out _);

        if (removed)
        {
            _logger.LogDebug("Removed session {SessionId} from registry", sessionId);
        }

        return removed;
    }

    /// <inheritdoc />
    public LiveCallSummary? UpdateHealth(string sessionId, Action<LiveCallSummary> updateAction)
    {
        ArgumentNullException.ThrowIfNull(updateAction);

        if (!_calls.TryGetValue(sessionId, out var summary))
        {
            _logger.LogWarning("Attempted to update health for non-existent session: {SessionId}", sessionId);
            return null;
        }

        updateAction(summary);

        _logger.LogDebug(
            "Updated health metrics for session {SessionId}. CustomerSentiment: {CustomerSentiment}, EscalationRisk: {EscalationRisk}",
            sessionId,
            summary.CustomerSentiment,
            summary.EscalationRiskScore);

        OnCallHealthUpdated(summary.Clone());

        return summary.Clone();
    }

    private void OnCallStarted(LiveCallSummary summary)
    {
        lock (_eventLock)
        {
            try
            {
                CallStarted?.Invoke(this, summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error raising CallStarted event for session {SessionId}", summary.SessionId);
            }
        }
    }

    private void OnCallEnded(LiveCallSummary summary)
    {
        lock (_eventLock)
        {
            try
            {
                CallEnded?.Invoke(this, summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error raising CallEnded event for session {SessionId}", summary.SessionId);
            }
        }
    }

    private void OnCallHealthUpdated(LiveCallSummary summary)
    {
        lock (_eventLock)
        {
            try
            {
                CallHealthUpdated?.Invoke(this, summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error raising CallHealthUpdated event for session {SessionId}", summary.SessionId);
            }
        }
    }
}
