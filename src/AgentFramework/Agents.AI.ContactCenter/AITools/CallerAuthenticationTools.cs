using System.ComponentModel;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.Extensions.AITools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Ready-made <see cref="IAIToolCollection"/> exposing the canonical elevation tools an
/// agent needs to drive PIN- and SMS-OTP-based caller authentication. Each tool wires
/// the per-call <see cref="ICallerElevationDispatcher"/> + the matching attempt buffer
/// automatically, so hosts don't have to thread <see cref="PinAttempt"/> /
/// <see cref="SmsOtpAttempt"/> through their own tool implementations.
/// </summary>
/// <remarks>
/// Register via <c>builder.AddCallerAuthenticationTools()</c>. Tools no-op gracefully
/// when the matching authenticator isn't registered, so this collection can be added
/// unconditionally and the host opts in per-authenticator with
/// <c>AddPinAuthenticator&lt;T&gt;()</c> / <c>AddCallerAuthenticator&lt;SmsOtpAuthenticator&gt;()</c>.
/// All tools are gated behind <see cref="CallerVerificationLevel.AniMatch"/> via
/// <see cref="RequiresCallerVerificationAttribute"/> — anonymous callers can't elevate
/// themselves blindly.
/// </remarks>
public sealed class CallerAuthenticationTools : IAIToolCollection
{
    private readonly ICallerElevationDispatcher _dispatcher;
    private readonly ICallSessionAccessor? _sessionAccessor;
    private readonly PinAttempt? _pinAttempt;
    private readonly SmsOtpAttempt? _otpAttempt;
    private readonly ILogger<CallerAuthenticationTools> _logger;

    public CallerAuthenticationTools(
        ICallerElevationDispatcher dispatcher,
        ICallSessionAccessor? sessionAccessor = null,
        PinAttempt? pinAttempt = null,
        SmsOtpAttempt? otpAttempt = null,
        ILogger<CallerAuthenticationTools>? logger = null)
    {
        _dispatcher = dispatcher;
        _sessionAccessor = sessionAccessor;
        _pinAttempt = pinAttempt;
        _otpAttempt = otpAttempt;
        _logger = logger ?? NullLogger<CallerAuthenticationTools>.Instance;
    }

    private string CallId => _sessionAccessor?.Current?.CallId ?? "tool-elevation";

    [Description(
        "Validate the caller's PIN against the on-file value. " +
        "On success the caller is elevated to KnowledgeBased verification. " +
        "Call this only after the caller has spoken or keyed their PIN.")]
    [RequiresCallerVerification(
        CallerVerificationLevel.AniMatch,
        FailureMessage = "Caller must be identified before a PIN can be validated.")]
    public async Task<CallerElevationToolResult> ValidatePinAsync(
        [Description("The PIN digits the caller supplied (leading zeros preserved).")] string digits,
        CancellationToken cancellationToken = default)
    {
        if (_pinAttempt is null)
        {
            _logger.LogWarning("ValidatePinAsync invoked but no PinAttempt is registered. Did you call AddPinAuthenticator<T>()?");
            return CallerElevationToolResult.Disabled("PIN authenticator not configured.");
        }
        _pinAttempt.Digits = digits;
        var run = await _dispatcher.DispatchAsync("Pin", CallId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return CallerElevationToolResult.From(run, "Pin");
    }

    [Description(
        "Send a one-time code (OTP) to the caller's registered phone number for multi-factor verification. " +
        "Returns the challenge id the caller-supplied code must reference, plus a prompt to read to the caller.")]
    [RequiresCallerVerification(
        CallerVerificationLevel.AniMatch,
        FailureMessage = "Caller must be identified before an OTP can be issued.")]
    public async Task<CallerElevationToolResult> RequestSmsOtpAsync(CancellationToken cancellationToken = default)
    {
        if (_otpAttempt is null)
        {
            _logger.LogWarning("RequestSmsOtpAsync invoked but no SmsOtpAttempt is registered.");
            return CallerElevationToolResult.Disabled("SMS OTP authenticator not configured.");
        }
        // Phase 1: no code yet — the authenticator returns NeedsChallenge.
        _otpAttempt.Code = null;
        _otpAttempt.ChallengeId = null;
        var run = await _dispatcher.DispatchAsync("SmsOtp", CallId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return CallerElevationToolResult.From(run, "SmsOtp");
    }

    [Description(
        "Submit the OTP code the caller supplied for an in-flight SMS-OTP challenge. " +
        "On success the caller is elevated to MultiFactor verification.")]
    [RequiresCallerVerification(
        CallerVerificationLevel.AniMatch,
        FailureMessage = "Caller must be identified before an OTP can be validated.")]
    public async Task<CallerElevationToolResult> SubmitSmsOtpAsync(
        [Description("The challenge id returned by RequestSmsOtpAsync.")] string challengeId,
        [Description("The OTP code the caller spoke or keyed.")] string code,
        CancellationToken cancellationToken = default)
    {
        if (_otpAttempt is null)
        {
            _logger.LogWarning("SubmitSmsOtpAsync invoked but no SmsOtpAttempt is registered.");
            return CallerElevationToolResult.Disabled("SMS OTP authenticator not configured.");
        }
        _otpAttempt.ChallengeId = challengeId;
        _otpAttempt.Code = code;
        var run = await _dispatcher.DispatchAsync("SmsOtp", CallId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return CallerElevationToolResult.From(run, "SmsOtp");
    }

    public IEnumerable<AITool> AsAITools()
    {
        if (_pinAttempt is not null)
        {
            yield return AIFunctionFactory.Create(ValidatePinAsync);
        }
        if (_otpAttempt is not null)
        {
            yield return AIFunctionFactory.Create(RequestSmsOtpAsync);
            yield return AIFunctionFactory.Create(SubmitSmsOtpAsync);
        }
    }
}

/// <summary>
/// Envelope returned from <see cref="CallerAuthenticationTools"/> elevation calls.
/// Designed to be small + agent-readable: <see cref="Success"/> drives prompt branching,
/// <see cref="Level"/> exposes the resulting verification level, and the optional
/// <see cref="ChallengeId"/> / <see cref="ChallengePrompt"/> let the agent read the OTP
/// instructions back to the caller.
/// </summary>
public sealed record CallerElevationToolResult(
    bool Success,
    CallerVerificationLevel Level,
    string Message,
    string? ChallengeId = null,
    string? ChallengePrompt = null)
{
    internal static CallerElevationToolResult From(AuthenticationRunResult run, string authenticatorName)
    {
        var step = run.Steps.LastOrDefault(s => string.Equals(s.AuthenticatorName, authenticatorName, StringComparison.OrdinalIgnoreCase));
        return step?.Outcome switch
        {
            AuthenticationOutcome.Authenticated => new CallerElevationToolResult(true, run.Identity.VerificationLevel, "Verified."),
            AuthenticationOutcome.Failed failed => new CallerElevationToolResult(false, run.Identity.VerificationLevel, failed.Reason),
            AuthenticationOutcome.NeedsChallenge needs => new CallerElevationToolResult(
                false, run.Identity.VerificationLevel, needs.Challenge.Prompt, needs.Challenge.ChallengeId, needs.Challenge.Prompt),
            AuthenticationOutcome.NotApplicable na => new CallerElevationToolResult(false, run.Identity.VerificationLevel, na.Reason),
            _ => new CallerElevationToolResult(false, run.Identity.VerificationLevel, $"Authenticator '{authenticatorName}' did not run."),
        };
    }

    internal static CallerElevationToolResult Disabled(string message)
        => new(false, CallerVerificationLevel.None, message);
}
