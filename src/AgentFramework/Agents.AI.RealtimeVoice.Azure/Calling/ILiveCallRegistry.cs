using Agents.AI.RealtimeVoice.Azure.Calling.Models;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

/// <summary>
/// Registry for tracking active calls in-memory for the operator dashboard.
/// </summary>
public interface ILiveCallRegistry
{
    /// <summary>
    /// Gets all currently active calls.
    /// </summary>
    /// <returns>A collection of active call summaries.</returns>
    IReadOnlyCollection<LiveCallSummary> GetActiveCalls();

    /// <summary>
    /// Gets a call summary by session ID.
    /// </summary>
    /// <param name="sessionId">The session ID to look up.</param>
    /// <returns>The call summary if found; otherwise null.</returns>
    LiveCallSummary? GetBySessionId(string sessionId);

    /// <summary>
    /// Creates or updates a call summary in the registry.
    /// </summary>
    /// <param name="summary">The call summary to upsert.</param>
    void Upsert(LiveCallSummary summary);

    /// <summary>
    /// Marks a session as ended with the specified end time.
    /// </summary>
    /// <param name="sessionId">The session ID to end.</param>
    /// <param name="endedAt">The timestamp when the session ended.</param>
    /// <returns>The updated call summary if found; otherwise null.</returns>
    LiveCallSummary? EndSession(string sessionId, DateTimeOffset endedAt);

    /// <summary>
    /// Removes a session from the registry.
    /// </summary>
    /// <param name="sessionId">The session ID to remove.</param>
    /// <returns>True if the session was removed; otherwise false.</returns>
    bool Remove(string sessionId);

    /// <summary>
    /// Updates health metrics for a session.
    /// </summary>
    /// <param name="sessionId">The session ID to update.</param>
    /// <param name="updateAction">Action to apply health metric updates.</param>
    /// <returns>The updated call summary if found; otherwise null.</returns>
    LiveCallSummary? UpdateHealth(string sessionId, Action<LiveCallSummary> updateAction);

    /// <summary>
    /// Event raised when a call starts (new session registered).
    /// </summary>
    event EventHandler<LiveCallSummary>? CallStarted;

    /// <summary>
    /// Event raised when a call ends.
    /// </summary>
    event EventHandler<LiveCallSummary>? CallEnded;

    /// <summary>
    /// Event raised when call health metrics are updated.
    /// </summary>
    event EventHandler<LiveCallSummary>? CallHealthUpdated;
}
