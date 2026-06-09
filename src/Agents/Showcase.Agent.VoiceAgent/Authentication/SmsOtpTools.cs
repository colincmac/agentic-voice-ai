using System.ComponentModel;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.Extensions.AITools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Showcase.Agent.VoiceAgent.Authentication;

/// <summary>
/// Showcase AI tools wired into the YAML IVR workflows for SMS-OTP MFA. Same shape as
/// <see cref="PinValidationTools"/>: each tool routes through <see cref="ICallerElevationDispatcher"/>
/// and the per-call <see cref="SmsOtpAttempt"/> so state is never mutated directly.
/// </summary>
/// <remarks>
/// The DTMF tier invokes <c>submit-otp</c> as a digit-collection validator (so it only
/// receives <c>digits</c>); the realtime tier may invoke <c>request-otp</c> first to
/// issue the challenge, then <c>submit-otp</c> with the digits the caller read back.
/// The challenge id is pulled from <see cref="CallerAuthenticationState.PendingChallenge"/>
/// (populated by <see cref="CallerElevationDispatcher"/> when the authenticator returns
/// <see cref="AuthenticationOutcome.NeedsChallenge"/>), so the model doesn't have to
/// memorize and pass it back through.
/// </remarks>
public static class SmsOtpTools
{
    /// <summary>
    /// Build the realtime-tier tool that issues a fresh OTP challenge. Returns a small
    /// envelope so the agent can read the resulting prompt ("a code has been sent to the
    /// number ending in …") back to the caller.
    /// </summary>
    public static AITool RequestOtpTool(ILoggerFactory? loggerFactory = null)
    {
        var logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("RequestOtp");

        [Description("Send a one-time passcode by SMS to the caller's phone on file. Call this once before asking the caller to read back the code. Returns success once the SMS is dispatched.")]
        [RequiresCallerVerification(CallerVerificationLevel.AniMatch, FailureMessage = "Caller must be identified by ANI before an OTP can be issued.")]
        async Task<OtpRequestResult> RequestOtp(IServiceProvider services, CancellationToken cancellationToken)
            => await IssueAsync(services, logger, cancellationToken).ConfigureAwait(false);

        return AIFunctionFactory.Create((Delegate)RequestOtp);
    }

    /// <summary>
    /// Build the tool both tiers use to submit the digits the caller supplied. Reads the
    /// in-flight challenge id from <see cref="CallerAuthenticationState.PendingChallenge"/>
    /// (set by the dispatcher when <c>request-otp</c> ran), so the caller-supplied
    /// <c>digits</c> is the only parameter.
    /// </summary>
    public static AITool SubmitOtpTool(ILoggerFactory? loggerFactory = null)
    {
        var logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("SubmitOtp");

        [Description("Submit the 6-digit OTP code the caller just supplied (spoken or via DTMF). Returns success once the caller is elevated to multi-factor verification.")]
        [RequiresCallerVerification(CallerVerificationLevel.AniMatch, FailureMessage = "Caller must be identified by ANI before an OTP can be validated.")]
        async Task<AuthValidationResult> SubmitOtp(
            [Description("The OTP digits the caller supplied (leading zeros preserved; non-digits ignored).")] string digits,
            IServiceProvider services,
            CancellationToken cancellationToken)
            => await SubmitAsync(digits, services, logger, cancellationToken).ConfigureAwait(false);

        return AIFunctionFactory.Create((Delegate)SubmitOtp);
    }

    private static async Task<OtpRequestResult> IssueAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var state = services.GetRequiredService<CallerAuthenticationState>();
        if (state.Identity.UserId == CallerIdentity.Anonymous.UserId)
        {
            logger.LogInformation("OTP request skipped: caller not yet identified.");
            return new OtpRequestResult(false, null, null, "Caller not identified.");
        }

        var dispatcher = services.GetService<ICallerElevationDispatcher>();
        var attempt = services.GetService<SmsOtpAttempt>();
        if (dispatcher is null || attempt is null)
        {
            logger.LogWarning(
                "OTP request unavailable: SmsOtpAuthenticator pipeline is not wired (dispatcher={Dispatcher}, attempt={Attempt})",
                dispatcher is not null, attempt is not null);
            return new OtpRequestResult(false, null, null, "OTP authenticator not configured.");
        }

        // Phase 1: empty attempt triggers NeedsChallenge.
        attempt.Code = null;
        attempt.ChallengeId = null;

        var run = await dispatcher.DispatchAsync("SmsOtp", "RequestOtp", cancellationToken: cancellationToken).ConfigureAwait(false);
        var step = run.Steps.LastOrDefault(s => string.Equals(s.AuthenticatorName, "SmsOtp", StringComparison.Ordinal));
        return step?.Outcome switch
        {
            AuthenticationOutcome.NeedsChallenge needs => new OtpRequestResult(true, needs.Challenge.ChallengeId, needs.Challenge.Prompt, "Code sent."),
            AuthenticationOutcome.Failed failed => new OtpRequestResult(false, null, null, failed.Reason),
            AuthenticationOutcome.NotApplicable na => new OtpRequestResult(false, null, null, na.Reason),
            _ => new OtpRequestResult(false, null, null, "OTP challenge did not start."),
        };
    }

    private static async Task<AuthValidationResult> SubmitAsync(
        string digits,
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var state = services.GetRequiredService<CallerAuthenticationState>();
        if (state.Identity.UserId == CallerIdentity.Anonymous.UserId)
        {
            return new AuthValidationResult(false, "Caller not identified.");
        }

        var pending = state.PendingChallenge;
        if (pending is null || pending.Method != AuthenticationMethod.SmsOtp || string.IsNullOrEmpty(pending.ChallengeId))
        {
            return new AuthValidationResult(false, "No OTP challenge in flight. Ask the caller to request a new code.");
        }

        var dispatcher = services.GetService<ICallerElevationDispatcher>();
        var attempt = services.GetService<SmsOtpAttempt>();
        if (dispatcher is null || attempt is null)
        {
            logger.LogWarning(
                "OTP submission unavailable: SmsOtpAuthenticator pipeline is not wired (dispatcher={Dispatcher}, attempt={Attempt})",
                dispatcher is not null, attempt is not null);
            return new AuthValidationResult(false, "OTP authenticator not configured.");
        }

        var normalized = new string((digits ?? string.Empty).Where(char.IsDigit).ToArray());
        if (string.IsNullOrEmpty(normalized))
        {
            return new AuthValidationResult(false, "I didn't catch any digits. Please read the code again.");
        }

        attempt.ChallengeId = pending.ChallengeId;
        attempt.Code = normalized;

        var run = await dispatcher.DispatchAsync("SmsOtp", "SubmitOtp", cancellationToken: cancellationToken).ConfigureAwait(false);
        var step = run.Steps.LastOrDefault(s => string.Equals(s.AuthenticatorName, "SmsOtp", StringComparison.Ordinal));
        return step?.Outcome switch
        {
            AuthenticationOutcome.Authenticated => new AuthValidationResult(true, "Verified."),
            AuthenticationOutcome.Failed failed => new AuthValidationResult(false, failed.Reason),
            _ => new AuthValidationResult(false, "OTP validation did not run."),
        };
    }
}

/// <summary>Envelope returned by <c>request-otp</c>.</summary>
public sealed record OtpRequestResult(bool Success, string? ChallengeId, string? Prompt, string Message);
