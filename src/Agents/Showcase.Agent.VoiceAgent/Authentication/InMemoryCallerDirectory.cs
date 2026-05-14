using System.Collections.Concurrent;
using Agents.AI.ContactCenter.Authentication;
using Microsoft.Extensions.Logging;

namespace Showcase.Agent.VoiceAgent.Authentication;

/// <summary>
/// Demo <see cref="ICallerDirectory"/> implementation. Hosts a small set of seeded customer
/// records so the showcase can resolve callers by ANI without a real CRM dependency.
/// Replace with a real lookup service in production.
/// </summary>
public sealed class InMemoryCallerDirectory : ICallerDirectory
{
    private readonly ILogger<InMemoryCallerDirectory> _logger;
    private readonly ConcurrentDictionary<string, CallerIdentity> _byPhone = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryCallerDirectory(ILogger<InMemoryCallerDirectory> logger)
    {
        _logger = logger;

        Seed(new CallerIdentity(
            UserId: "cust-001",
            DisplayName: "Jordan Reyes",
            PhoneNumber: "+15551234567",
            Email: "jordan.reyes@example.com",
            EntraObjectId: null,
            VerificationLevel: CallerVerificationLevel.AniMatch,
            AuthenticatedAt: DateTimeOffset.UtcNow,
            AuthenticatedBy: nameof(AniIdentityLookupAuthenticator),
            Claims: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["accountTier"] = "Premium",
                ["preferredLanguage"] = "en-US",
                ["pin"] = "4242"
            }));

        Seed(new CallerIdentity(
            UserId: "cust-002",
            DisplayName: "Sam Patel",
            PhoneNumber: "+15559876543",
            Email: "sam.patel@example.com",
            EntraObjectId: null,
            VerificationLevel: CallerVerificationLevel.AniMatch,
            AuthenticatedAt: DateTimeOffset.UtcNow,
            AuthenticatedBy: nameof(AniIdentityLookupAuthenticator),
            Claims: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["accountTier"] = "Standard",
                ["preferredLanguage"] = "es-MX",
                ["pin"] = "1357"
            }));
    }

    public Task<CallerIdentity?> FindByPhoneNumberAsync(string phoneNumberE164, CancellationToken cancellationToken = default)
    {
        var match = _byPhone.TryGetValue(phoneNumberE164, out var identity) ? identity : null;
        if (match is null)
        {
            _logger.LogInformation("No directory entry for {Phone}", phoneNumberE164);
        }
        else
        {
            _logger.LogInformation("Resolved {UserId} ({DisplayName}) for {Phone}", match.UserId, match.DisplayName, phoneNumberE164);
        }
        return Task.FromResult(match);
    }

    /// <summary>Lookup by user id; used by <see cref="PinChallengeAuthenticator"/> to verify PINs.</summary>
    internal CallerIdentity? FindByUserId(string userId)
        => _byPhone.Values.FirstOrDefault(i => string.Equals(i.UserId, userId, StringComparison.OrdinalIgnoreCase));

    private void Seed(CallerIdentity identity)
    {
        if (identity.PhoneNumber is { Length: > 0 } phone)
        {
            _byPhone[phone] = identity;
        }
    }
}
