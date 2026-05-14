using System.Collections.Concurrent;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Authentication;

namespace Showcase.Agent.VoiceAgent.Authentication;

/// <summary>
/// Singleton in-memory store of caller-authentication state per active call. Populated by
/// <see cref="CallerAuthStateObserver"/> as it reads <see cref="StrategyEvent"/>s, queried
/// by the showcase diagnostics endpoint. Demo-only — production deployments would push to
/// a session store / call analytics pipeline instead.
/// </summary>
public sealed class CallerAuthStateRegistry
{
    private readonly ConcurrentDictionary<string, CallerAuthRecord> _byCall = new();

    public CallerAuthRecord? TryGet(string callId)
        => _byCall.TryGetValue(callId, out var record) ? record : null;

    public IReadOnlyCollection<KeyValuePair<string, CallerAuthRecord>> Snapshot()
        => _byCall.ToArray();

    internal CallerAuthRecord GetOrAdd(string callId)
        => _byCall.GetOrAdd(callId, static _ => new CallerAuthRecord());

    internal void Remove(string callId) => _byCall.TryRemove(callId, out _);
}

/// <summary>Mutable per-call record updated by <see cref="CallerAuthStateObserver"/>.</summary>
public sealed class CallerAuthRecord
{
    public CallerIdentity Identity { get; internal set; } = CallerIdentity.Anonymous;
    public CallerVerificationLevel VerificationLevel { get; internal set; } = CallerVerificationLevel.None;
    public AuthenticationChallenge? PendingChallenge { get; internal set; }
    public List<AuthenticationStep> Steps { get; } = [];
}
