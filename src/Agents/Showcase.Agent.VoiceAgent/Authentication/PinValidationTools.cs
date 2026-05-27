using System.ComponentModel;
using Agents.AI.ContactCenter.Authentication;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Showcase.Agent.VoiceAgent.Authentication;

/// <summary>
/// AI tools wired into the showcase workflows so callers can elevate their verification level
/// during the call. The DTMF workflow exposes <see cref="ValidatePinTool"/> as a digit-collection
/// validator; the realtime workflow exposes <see cref="ConfirmIdentityTool"/> for the model to
/// call once the caller has answered the PIN prompt.
/// </summary>
/// <remarks>
/// Both tools route through <see cref="ICallerElevationDispatcher"/> — they set
/// <see cref="PinAttempt.Digits"/> and dispatch the <c>Pin</c> authenticator. State is never
/// mutated directly. The <see cref="RequiresCallerVerificationAttribute"/> ensures the caller
/// has already cleared ANI lookup before a PIN attempt is even accepted.
/// </remarks>
public static class PinValidationTools
{
    /// <summary>
    /// Build the validator tool the DTMF strategy invokes when the caller has finished
    /// entering their PIN. Returns an envelope with <c>Success</c>; the navigator interprets
    /// it as a transition to the success step on true, or a reject on false.
    /// </summary>
    public static AITool ValidatePinTool(
        InMemoryCallerDirectory directory,
        ILoggerFactory? loggerFactory = null)
    {
        var logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("ValidatePin");

        [Description("Validate the caller's entered PIN against their account on file. Returns success when the PIN matches and the caller is elevated to full verification.")]
        [RequiresCallerVerification(CallerVerificationLevel.AniMatch, FailureMessage = "Caller must be identified by ANI before a PIN can be validated.")]
        AuthValidationResult ValidatePin(
            [Description("The PIN digits the caller supplied (with leading zeros preserved).")] string digits,
            IServiceProvider services)
            => Validate(digits, directory, services, logger, "PinValidation");

        return AIFunctionFactory.Create((Delegate)ValidatePin);
    }

    /// <summary>
    /// Build a tool the realtime model calls to confirm the caller's PIN. Same logic as
    /// <see cref="ValidatePinTool"/> but exposed under a model-friendly name and description.
    /// </summary>
    public static AITool ConfirmIdentityTool(
        InMemoryCallerDirectory directory,
        ILoggerFactory? loggerFactory = null)
    {
        var logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("ConfirmIdentity");

        [Description("Verify the caller by checking the four-digit PIN they just spoke against the account on file. Call this only once the caller has stated their PIN clearly.")]
        [RequiresCallerVerification(CallerVerificationLevel.AniMatch, FailureMessage = "Caller must be identified by ANI before a PIN can be validated.")]
        AuthValidationResult ConfirmIdentity(
            [Description("The four-digit PIN the caller spoke aloud.")] string pin,
            IServiceProvider services)
            => Validate(pin, directory, services, logger, "ConfirmIdentity");

        return AIFunctionFactory.Create((Delegate)ConfirmIdentity);
    }

    private static AuthValidationResult Validate(
        string digits,
        InMemoryCallerDirectory directory,
        IServiceProvider services,
        ILogger logger,
        string callId)
    {
        var state = services.GetRequiredService<CallerAuthenticationState>();
        if (state.Identity.UserId == "anonymous")
        {
            logger.LogInformation("PIN validation skipped: caller is not yet identified");
            return new AuthValidationResult(false, "Caller not identified.");
        }

        var dispatcher = services.GetService<ICallerElevationDispatcher>();
        var attempt = services.GetService<PinAttempt>();
        if (dispatcher is null || attempt is null)
        {
            logger.LogWarning(
                "Falling back to direct validation: PinAuthenticator pipeline is not wired (dispatcher={Dispatcher}, attempt={Attempt})",
                dispatcher is not null, attempt is not null);
            return DirectValidate(digits, directory, state, logger, callId);
        }

        attempt.Digits = digits;
        var result = dispatcher.DispatchAsync("Pin", callId).GetAwaiter().GetResult();
        var pinStep = result.Steps.LastOrDefault(s => s.AuthenticatorName == "Pin");
        return pinStep?.Outcome switch
        {
            AuthenticationOutcome.Authenticated => new AuthValidationResult(true, "Verified."),
            AuthenticationOutcome.Failed failed => new AuthValidationResult(false, failed.Reason),
            _ => new AuthValidationResult(false, "PIN validation did not run; check PinAuthenticator registration.")
        };
    }

    private static AuthValidationResult DirectValidate(
        string digits,
        InMemoryCallerDirectory directory,
        CallerAuthenticationState state,
        ILogger logger,
        string authenticatorName)
    {
        var record = directory.FindByUserId(state.Identity.UserId);
        var expected = record?.Claims.TryGetValue("pin", out var pin) == true ? pin?.ToString() : null;
        if (string.IsNullOrEmpty(expected))
        {
            logger.LogWarning("Caller {UserId} has no PIN on file", state.Identity.UserId);
            return new AuthValidationResult(false, "No PIN on file for this caller.");
        }

        if (!string.Equals(expected, digits, StringComparison.Ordinal))
        {
            logger.LogInformation("PIN mismatch for caller {UserId}", state.Identity.UserId);
            return new AuthValidationResult(false, "Incorrect PIN.");
        }

        var elevated = state.Identity with
        {
            VerificationLevel = CallerVerificationLevel.KnowledgeBased,
            AuthenticatedBy = authenticatorName,
            AuthenticatedAt = DateTimeOffset.UtcNow
        };
        state.TryPromote(elevated);
        logger.LogInformation("Caller {UserId} elevated to KnowledgeBased via {Authenticator}", state.Identity.UserId, authenticatorName);
        return new AuthValidationResult(true, "Verified.");
    }
}

/// <summary>
/// Envelope returned from auth tools. The IVR navigator interprets the
/// <see cref="Success"/> field via reflection to decide between transition and reject.
/// </summary>
public sealed record AuthValidationResult(bool Success, string Message);
