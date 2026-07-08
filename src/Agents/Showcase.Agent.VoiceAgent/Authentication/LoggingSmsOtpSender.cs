using System.Collections.Concurrent;
using Agents.AI.ContactCenter.Authentication;
using Microsoft.Extensions.Logging;

namespace Showcase.Agent.VoiceAgent.Authentication;

/// <summary>
/// Demo <see cref="ISmsOtpSender"/> implementation. Does NOT actually send an SMS — it
/// logs the generated code at <see cref="LogLevel.Information"/> and stores the most
/// recent code in a process-wide <see cref="LastIssuedOtpRegistry"/> so the
/// <c>api/diagnostics/auth</c> endpoint (and tests) can read it back during an E2E
/// smoke run. Replace with an ACS SMS / Twilio sender in production.
/// </summary>
public sealed class LoggingSmsOtpSender : ISmsOtpSender
{
    private readonly LastIssuedOtpRegistry _registry;
    private readonly ILogger<LoggingSmsOtpSender> _logger;

    public LoggingSmsOtpSender(LastIssuedOtpRegistry registry, ILogger<LoggingSmsOtpSender> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public Task SendAsync(string phoneNumberE164, string code, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[DEMO OTP] code={Code} to={Phone}", code, phoneNumberE164);
        _registry.Record(phoneNumberE164, code);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Process-wide registry of the most recent OTP code issued per phone number. Singleton
/// so the diagnostics endpoint can surface the code without touching per-call DI scope.
/// Demo-only — never store OTP codes in process memory in production.
/// </summary>
public sealed class LastIssuedOtpRegistry
{
    private readonly ConcurrentDictionary<string, LastIssuedOtp> _byPhone = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string phoneNumberE164, string code)
        => _byPhone[phoneNumberE164] = new LastIssuedOtp(phoneNumberE164, code, DateTimeOffset.UtcNow);

    public LastIssuedOtp? TryGet(string phoneNumberE164)
        => _byPhone.TryGetValue(phoneNumberE164, out var hit) ? hit : null;

    public IReadOnlyCollection<LastIssuedOtp> Snapshot() => _byPhone.Values.ToArray();
}

/// <summary>The most recent OTP issued for a phone number, surfaced by the diagnostics API.</summary>
public sealed record LastIssuedOtp(string PhoneNumberE164, string Code, DateTimeOffset IssuedAt);
