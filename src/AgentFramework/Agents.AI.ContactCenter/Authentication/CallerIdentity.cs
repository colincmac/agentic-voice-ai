using System.Globalization;
using System.Security.Claims;

namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Identity established for the caller on an active <see cref="ICallSession"/>.
/// One instance is built per successful authenticator, then merged into the
/// session-scoped <see cref="CallerAuthenticationState"/>.
/// </summary>
/// <param name="UserId">Stable user identifier in the back-office system (account number, CRM id, etc.).</param>
/// <param name="DisplayName">Human-readable name to address the caller in conversation.</param>
/// <param name="PhoneNumber">E.164 phone number associated with this identity, if known.</param>
/// <param name="Email">Email associated with this identity, if known.</param>
/// <param name="EntraObjectId">Microsoft Entra object id, if the caller is an enterprise user.</param>
/// <param name="VerificationLevel">Strength of the verification that produced this identity.</param>
/// <param name="AuthenticatedAt">UTC timestamp at which the verifying authenticator finished.</param>
/// <param name="AuthenticatedBy">Name of the <see cref="ICallerAuthenticator"/> that produced this identity.</param>
/// <param name="Claims">Arbitrary claims surfaced by the authenticator (department, tier, account flags, etc.).</param>
public sealed record CallerIdentity(
    string UserId,
    string DisplayName,
    string? PhoneNumber,
    string? Email,
    string? EntraObjectId,
    CallerVerificationLevel VerificationLevel,
    DateTimeOffset AuthenticatedAt,
    string AuthenticatedBy,
    IReadOnlyDictionary<string, object?> Claims)
{
    public static CallerIdentity Anonymous { get; } = new(
        UserId: "anonymous",
        DisplayName: "Anonymous Caller",
        PhoneNumber: null,
        Email: null,
        EntraObjectId: null,
        VerificationLevel: CallerVerificationLevel.None,
        AuthenticatedAt: DateTimeOffset.MinValue,
        AuthenticatedBy: "(none)",
        Claims: new Dictionary<string, object?>());

    /// <summary>
    /// Returns a copy of this identity with the provided <paramref name="level"/> if it is stronger
    /// than the current one, otherwise returns the current instance.
    /// </summary>
    public CallerIdentity WithAtLeast(CallerVerificationLevel level)
        => level > VerificationLevel ? this with { VerificationLevel = level } : this;

    public ClaimsPrincipal ToClaimsPrincipal()
    {
        var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, UserId),
        new(ClaimTypes.Name, DisplayName),
        // acr/amr are the standard OIDC vocabulary for "level of assurance" and "methods used"
        new("acr", ((int)VerificationLevel).ToString(CultureInfo.InvariantCulture)),
        new("amr", AuthenticatedBy),
        new("auth_time", AuthenticatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
    };

        if (PhoneNumber is not null) { claims.Add(new Claim("phone_number", PhoneNumber)); }
        if (Email is not null) { claims.Add(new Claim(ClaimTypes.Email, Email)); }
        if (EntraObjectId is not null) { claims.Add(new Claim("oid", EntraObjectId)); }

        foreach (var (key, value) in Claims)
        {
            if (value is not null) { claims.Add(new Claim(key, value.ToString() ?? string.Empty)); }
        }

        var identity = new ClaimsIdentity(claims, authenticationType: AuthenticatedBy);
        return new ClaimsPrincipal(identity);
    }
}
