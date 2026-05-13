namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed;

/// <summary>
/// Scoped accessor that exposes the <see cref="ICallSession"/> currently being
/// served by this DI scope. Set by <c>CallSessionFactory</c> when it builds the
/// session, then resolved by AI tool collections (e.g. <c>CallControlTools</c>)
/// so AI agents can hang up / transfer the live call.
/// </summary>
public interface ICallSessionAccessor
{
    /// <summary>The session bound to this scope, or <see langword="null"/> if none was set.</summary>
    ICallSession? Current { get; }

    /// <summary>Bind <paramref name="session"/> as the current session for this scope. Throws if already set.</summary>
    void Set(ICallSession session);
}

/// <summary>
/// Default in-memory implementation. Single-assignment: a scope serves exactly
/// one session for its lifetime, which matches how <c>CallSessionFactory</c>
/// creates one DI scope per call.
/// </summary>
public sealed class CallSessionAccessor : ICallSessionAccessor
{
    private ICallSession? _current;

    public ICallSession? Current => _current;

    public void Set(ICallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Interlocked.CompareExchange(ref _current, session, null) is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(CallSessionAccessor)} already bound to call '{_current!.CallId}'.");
        }
    }
}
