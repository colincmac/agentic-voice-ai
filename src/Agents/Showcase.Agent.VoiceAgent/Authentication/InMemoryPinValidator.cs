using Agents.AI.ContactCenter.Authentication;
using Microsoft.Extensions.Logging;

namespace Showcase.Agent.VoiceAgent.Authentication;

/// <summary>
/// Demo <see cref="IPinValidator"/> backed by the seeded <see cref="InMemoryCallerDirectory"/>.
/// Reads the expected PIN out of the caller's <see cref="CallerIdentity.Claims"/> bag under
/// the <c>pin</c> key. Replace with a real validator (vault / banking core / Entra) in
/// production.
/// </summary>
public sealed class InMemoryPinValidator : IPinValidator
{
    private readonly InMemoryCallerDirectory _directory;
    private readonly ILogger<InMemoryPinValidator> _logger;

    public InMemoryPinValidator(InMemoryCallerDirectory directory, ILogger<InMemoryPinValidator> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    public Task<bool?> ValidateAsync(CallerIdentity identity, string digits, CancellationToken cancellationToken = default)
    {
        var record = _directory.FindByUserId(identity.UserId);
        var expected = record?.Claims.TryGetValue("pin", out var pin) == true ? pin?.ToString() : null;
        if (string.IsNullOrEmpty(expected))
        {
            _logger.LogWarning("No PIN on file for caller {UserId}", identity.UserId);
            return Task.FromResult<bool?>(null);
        }

        var match = string.Equals(expected, digits, StringComparison.Ordinal);
        if (!match)
        {
            _logger.LogInformation("PIN mismatch for caller {UserId}", identity.UserId);
        }
        return Task.FromResult<bool?>(match);
    }
}
