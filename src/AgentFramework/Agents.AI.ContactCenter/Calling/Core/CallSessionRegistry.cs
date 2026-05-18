using System.Collections.Concurrent;

namespace Agents.AI.ContactCenter.Calling.Core;

/// <summary>
/// Default in-process registry of active call sessions.
/// </summary>
public sealed class CallSessionRegistry : ICallSessionRegistry
{
    private readonly ConcurrentDictionary<string, ICallSession> _sessions = new();

    internal void Add(ICallSession session) => _sessions[session.CallId] = session;

    public ICallSession? TryGet(string callId)
    {
        _sessions.TryGetValue(callId, out var session);
        return session;
    }

    public IReadOnlyCollection<ICallSession> ActiveSessions => _sessions.Values.ToArray();

    public Task<bool> RemoveAsync(string callId, CancellationToken cancellationToken = default)
        => Task.FromResult(_sessions.TryRemove(callId, out _));
}
