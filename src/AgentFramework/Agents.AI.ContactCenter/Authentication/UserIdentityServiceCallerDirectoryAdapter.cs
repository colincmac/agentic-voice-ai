using System.Collections.Generic;
using Agents.AI.ContactCenter.Authentication.UserIdentity;

namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Bridges the legacy <see cref="IUserIdentityService"/> (which requires both name and
/// phone number) to the simpler <see cref="ICallerDirectory"/> contract used by
/// <see cref="AniIdentityLookupAuthenticator"/>. Looks up by phone number using empty
/// name parts so the wildcard match in the in-memory implementation succeeds.
/// </summary>
internal sealed class UserIdentityServiceCallerDirectoryAdapter : ICallerDirectory
{
    private readonly IUserIdentityService _userIdentityService;

    public UserIdentityServiceCallerDirectoryAdapter(IUserIdentityService userIdentityService)
    {
        _userIdentityService = userIdentityService;
    }

    public async Task<CallerIdentity?> FindByPhoneNumberAsync(string phoneNumberE164, CancellationToken cancellationToken = default)
    {
        // The legacy service's signature requires a name, but the in-memory implementation
        // does a fuzzy first/last-name OR match against parts. Pass a wildcard so any name
        // part will match — phone is the discriminator we actually have at ANI time.
        var match = await _userIdentityService
            .LookupUserAsync(name: " ", phoneNumber: phoneNumberE164, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (match is null) { return null; }

        var displayName = !string.IsNullOrWhiteSpace(match.FirstName) || !string.IsNullOrWhiteSpace(match.LastName)
            ? $"{match.FirstName} {match.LastName}".Trim()
            : match.UserPrincipalName ?? match.Email ?? match.UserId;

        var claims = new Dictionary<string, object?>(match.Claims.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in match.Claims) { claims[k] = v; }

        return new CallerIdentity(
            UserId: match.UserId,
            DisplayName: displayName,
            PhoneNumber: match.PhoneNumber ?? phoneNumberE164,
            Email: match.Email,
            EntraObjectId: match.EntraObjectId,
            VerificationLevel: CallerVerificationLevel.AniMatch,
            AuthenticatedAt: DateTimeOffset.UtcNow,
            AuthenticatedBy: nameof(AniIdentityLookupAuthenticator),
            Claims: claims);
    }
}
