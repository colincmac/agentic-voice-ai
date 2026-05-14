using System.Collections.Generic;
using System.Threading;

namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Per-call mutable container for the caller's evolving identity and the audit trail of
/// authenticator attempts. Registered as <c>Scoped</c> so a single instance is shared across
/// the strategy, observers, and any per-call AI tools.
/// </summary>
public sealed class CallerAuthenticationState
{
    private readonly object _gate = new();
    private CallerIdentity _identity = CallerIdentity.Anonymous;
    private readonly List<AuthenticationStep> _steps = [];
    private AuthenticationChallenge? _pendingChallenge;

    /// <summary>The strongest identity established for the caller so far.</summary>
    public CallerIdentity Identity
    {
        get { lock (_gate) { return _identity; } }
    }

    /// <summary>True once any authenticator has produced an <see cref="AuthenticationOutcome.Authenticated"/>.</summary>
    public bool IsAuthenticated
    {
        get { lock (_gate) { return _identity.VerificationLevel != CallerVerificationLevel.None; } }
    }

    /// <summary>Open challenge waiting on the caller. Null when no challenge is in flight.</summary>
    public AuthenticationChallenge? PendingChallenge
    {
        get { lock (_gate) { return _pendingChallenge; } }
    }

    /// <summary>Audit trail of every authenticator attempt for this call, in order.</summary>
    public IReadOnlyList<AuthenticationStep> Steps
    {
        get { lock (_gate) { return _steps.ToArray(); } }
    }

    /// <summary>Raised after <see cref="Identity"/> changes. Subscribers run synchronously under the lock.</summary>
    public event Action<CallerIdentity>? IdentityChanged;

    /// <summary>
    /// Apply the supplied <paramref name="identity"/> as the new identity if it strengthens the
    /// current one (higher <see cref="CallerVerificationLevel"/>) or replaces an anonymous identity.
    /// Returns <see langword="true"/> when <see cref="Identity"/> actually changed.
    /// </summary>
    public bool TryPromote(CallerIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        CallerIdentity? promoted = null;
        lock (_gate)
        {
            if (_identity.VerificationLevel == CallerVerificationLevel.None
                || identity.VerificationLevel > _identity.VerificationLevel
                || (identity.VerificationLevel == _identity.VerificationLevel
                    && _identity.UserId == "anonymous"))
            {
                _identity = identity;
                promoted = identity;
            }
        }

        if (promoted is not null)
        {
            IdentityChanged?.Invoke(promoted);
            return true;
        }
        return false;
    }

    /// <summary>Append an attempt to the audit trail.</summary>
    public void RecordStep(AuthenticationStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        lock (_gate) { _steps.Add(step); }
    }

    /// <summary>Set the open challenge; passing <see langword="null"/> clears it.</summary>
    public void SetPendingChallenge(AuthenticationChallenge? challenge)
    {
        lock (_gate) { _pendingChallenge = challenge; }
    }

    /// <summary>Reset to anonymous and clear any history. Used by tests and on session re-create.</summary>
    public void Reset()
    {
        CallerIdentity? cleared = null;
        lock (_gate)
        {
            if (_identity.VerificationLevel != CallerVerificationLevel.None)
            {
                _identity = CallerIdentity.Anonymous;
                cleared = _identity;
            }
            _steps.Clear();
            _pendingChallenge = null;
        }
        if (cleared is not null) { IdentityChanged?.Invoke(cleared); }
    }
}
