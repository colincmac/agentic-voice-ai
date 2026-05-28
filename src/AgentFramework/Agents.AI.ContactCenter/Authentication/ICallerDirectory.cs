namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Lightweight directory used by <see cref="AniIdentityLookupAuthenticator"/> to translate the
/// inbound caller's E.164 number into a <see cref="CallerIdentity"/>. Hosts plug their CRM /
/// customer database in by implementing this interface and registering it as a singleton.
/// </summary>
/// <remarks>
/// Returning <see langword="null"/> means "no record matched"; the authenticator will surface
/// an <see cref="AuthenticationOutcome.NotApplicable"/> so the orchestrator falls through to
/// stronger methods (e.g. MFA, biometric) that can establish identity from caller input.
/// </remarks>
public interface ICallerDirectory
{
    Task<CallerIdentity?> FindByPhoneNumberAsync(string phoneNumberE164, CancellationToken cancellationToken = default);
}
