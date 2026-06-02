using System.Reflection;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.Realtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.IvrWorkflow.Authorization;

/// <summary>
/// Function-invocation middleware that enforces <see cref="RequiresCallerVerificationAttribute"/>
/// at tool-invocation time as defense-in-depth against the model attempting to call a tool
/// it shouldn't have available. Mirrors the responsibility of the legacy
/// <c>CallerVerificationApprovalHandler</c> but runs as a single MEAI-style
/// <see cref="AgentFunctionInvocationMiddleware"/> rather than per-tool approval wrapping.
/// </summary>
/// <remarks>
/// Wire via <c>.UseFunctionInvocation(CallerVerificationFilter.Middleware)</c> or compose
/// with other middleware. Resolves <see cref="CallerAuthenticationState"/> from the
/// function's <see cref="AIFunctionArguments.Services"/>; when absent, fails closed.
/// </remarks>
public static class CallerVerificationFilter
{
    /// <summary>
    /// Invoke the filter. Returns the next middleware's result on allow, or a
    /// <see cref="TextContent"/>-shaped failure when the caller's verification level is
    /// below the required minimum.
    /// </summary>
    public static async ValueTask<object?> InvokeAsync(
        AIAgent? agent,
        AIFunctionArguments arguments,
        AIFunction function,
        Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(next);

        var requirement = ResolveRequirement(function);
        if (requirement is null)
        {
            return await next(arguments, cancellationToken).ConfigureAwait(false);
        }

        var logger = ResolveLogger(arguments, agent);
        var state = ResolveAuthenticationState(arguments, agent);
        if (state is null)
        {
            logger?.LogWarning(
                "CallerVerificationFilter: no CallerAuthenticationState resolved for tool '{Tool}' (required level '{Level}'); failing closed.",
                function.Name, requirement.MinimumLevel);
            return BuildDenial(requirement);
        }

        var current = state.Identity.VerificationLevel;
        if (current >= requirement.MinimumLevel)
        {
            return await next(arguments, cancellationToken).ConfigureAwait(false);
        }

        logger?.LogInformation(
            "CallerVerificationFilter: tool '{Tool}' denied — caller level '{Current}' < required '{Required}'.",
            function.Name, current, requirement.MinimumLevel);
        return BuildDenial(requirement);
    }

    /// <summary>Convenience instance matching the <see cref="AgentFunctionInvocationMiddleware"/> delegate.</summary>
    public static AgentFunctionInvocationMiddleware Middleware { get; } =
        (agent, args, fn, next, ct) => InvokeAsync(agent, args, fn, next, ct);

    private static RequiresCallerVerificationAttribute? ResolveRequirement(AIFunction function) =>
        function.UnderlyingMethod?.GetCustomAttribute<RequiresCallerVerificationAttribute>(inherit: true);

    private static CallerAuthenticationState? ResolveAuthenticationState(AIFunctionArguments arguments, AIAgent? agent) =>
        arguments.Services?.GetService<CallerAuthenticationState>()
        ?? agent?.GetService(typeof(CallerAuthenticationState)) as CallerAuthenticationState;

    private static ILogger? ResolveLogger(AIFunctionArguments arguments, AIAgent? agent)
    {
        var factory = arguments.Services?.GetService<ILoggerFactory>()
            ?? agent?.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        return factory?.CreateLogger(typeof(CallerVerificationFilter).FullName!) ?? (ILogger)NullLogger.Instance;
    }

    private static object BuildDenial(RequiresCallerVerificationAttribute requirement) =>
        requirement.FailureMessage
            ?? $"This action requires the caller to be verified at level '{requirement.MinimumLevel}' or higher.";
}
