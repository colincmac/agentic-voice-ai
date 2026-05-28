using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// <see cref="DelegatingAIFunction"/> that runs an inner tool and, when the inner tool
/// signals success, dispatches a named <see cref="ICallerAuthenticator"/> through
/// <see cref="ICallerElevationDispatcher"/> to elevate the caller. Use this when a host
/// already owns a tool that collects a secret (PIN digits, OTP code, biometric sample, …)
/// and just wants to bolt SDK-managed elevation onto the existing surface — no manual
/// dispatcher wiring required.
/// </summary>
/// <remarks>
/// The wrapper does NOT pass the caller-supplied secret into the dispatcher itself; the
/// inner tool is still responsible for stashing it on the matching attempt object
/// (<see cref="PinAttempt"/>, <see cref="SmsOtpAttempt"/>, …) before returning. This
/// keeps the secret out of the wrapper's signature and matches the per-authenticator
/// contract.
///
/// Detection of "success" is configurable via <see cref="successPredicate"/>; the default
/// treats any non-null, non-exception result whose <c>bool Success</c> property (if any)
/// is <see langword="true"/> as success, mirroring the showcase's <c>AuthValidationResult</c>
/// shape.
/// </remarks>
public sealed class ElevatingAIFunction : DelegatingAIFunction
{
    private readonly string _authenticatorName;
    private readonly Func<object?, bool> _successPredicate;
    private readonly ILogger<ElevatingAIFunction> _logger;

    public ElevatingAIFunction(
        AIFunction innerFunction,
        string authenticatorName,
        Func<object?, bool>? successPredicate = null,
        ILogger<ElevatingAIFunction>? logger = null) : base(innerFunction)
    {
        ArgumentException.ThrowIfNullOrEmpty(authenticatorName);
        _authenticatorName = authenticatorName;
        _successPredicate = successPredicate ?? DefaultSuccessPredicate;
        _logger = logger ?? NullLogger<ElevatingAIFunction>.Instance;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (!_successPredicate(result))
        {
            return result;
        }

        var dispatcher = arguments.Services?.GetService<ICallerElevationDispatcher>();
        if (dispatcher is null)
        {
            _logger.LogWarning(
                "ElevatingAIFunction wrapped '{Tool}' but no ICallerElevationDispatcher is registered; elevation skipped.",
                InnerFunction.Name);
            return result;
        }

        var callId = arguments.Services?.GetService<Calling.ICallSessionAccessor>()?.Current?.CallId
                     ?? InnerFunction.Name;
        try
        {
            await dispatcher.DispatchAsync(_authenticatorName, callId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ElevatingAIFunction dispatch '{Authenticator}' failed for tool '{Tool}'.", _authenticatorName, InnerFunction.Name);
        }
        return result;
    }

    /// <summary>
    /// Treats result as success when it is non-null and either has no <c>bool Success</c>
    /// member or that member is <see langword="true"/>.
    /// </summary>
    private static bool DefaultSuccessPredicate(object? result)
    {
        if (result is null)
        {
            return false;
        }
        var prop = result.GetType().GetProperty("Success", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (prop is null || prop.PropertyType != typeof(bool))
        {
            return true;
        }
        return (bool)(prop.GetValue(result) ?? false);
    }

    /// <summary>
    /// Fluent wrapper helper: <c>tool.WithElevation("Pin")</c>.
    /// </summary>
    public static ElevatingAIFunction Wrap(AIFunction inner, string authenticatorName, Func<object?, bool>? successPredicate = null)
        => new(inner, authenticatorName, successPredicate);
}
