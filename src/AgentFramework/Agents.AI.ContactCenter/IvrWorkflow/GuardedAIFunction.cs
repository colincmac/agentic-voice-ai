using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// <see cref="DelegatingAIFunction"/> that runs a set of <see cref="IIvrStepGuard"/> checks
/// against the live <see cref="IvrWorkflowState"/> before forwarding the call to the inner
/// function. If any guard fails, the function short-circuits and returns the guard's
/// failure reason as the tool result so the LLM (or any caller) can read it as a normal
/// tool response — mirroring the
/// <c>AuthorizingAgentFunction</c> approval pattern.
/// </summary>
/// <remarks>
/// Instances are intended to be created per-step (typically by
/// <see cref="WrapTools(IEnumerable{AITool}, IReadOnlyList{IIvrStepGuard}, Func{IvrWorkflowState}, ILoggerFactory?)"/>)
/// and discarded when the step changes. The <see cref="IvrWorkflowState"/> is read through
/// a <see cref="Func{IvrWorkflowState}"/> on every invocation so callers can share one
/// wrapper across calls without re-binding when state mutates.
/// </remarks>
public sealed class GuardedAIFunction : DelegatingAIFunction
{
    private readonly IReadOnlyList<IIvrStepGuard> _guards;
    private readonly Func<IvrWorkflowState> _stateAccessor;
    private readonly ILogger<GuardedAIFunction>? _logger;

    public GuardedAIFunction(
        AIFunction innerFunction,
        IReadOnlyList<IIvrStepGuard> guards,
        Func<IvrWorkflowState> stateAccessor,
        ILogger<GuardedAIFunction>? logger = null) : base(innerFunction)
    {
        ArgumentNullException.ThrowIfNull(innerFunction);
        ArgumentNullException.ThrowIfNull(guards);
        ArgumentNullException.ThrowIfNull(stateAccessor);

        _guards = guards;
        _stateAccessor = stateAccessor;
        _logger = logger;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (_guards.Count == 0)
        {
            return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

        var state = _stateAccessor();
        for (var i = 0; i < _guards.Count; i++)
        {
            var guard = _guards[i];
            var result = await guard.EvaluateAsync(state, cancellationToken).ConfigureAwait(false);
            if (!result.Passed)
            {
                var reason = result.FailureReason ?? "Action blocked by workflow guard.";
                _logger?.LogInformation(
                    "Tool '{FunctionName}' blocked by guard '{GuardType}': {Reason}",
                    InnerFunction.Name, guard.GetType().Name, reason);
                return $"Action blocked: {reason}";
            }
        }

        return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Wrap each <see cref="AIFunction"/> in <paramref name="tools"/> with a
    /// <see cref="GuardedAIFunction"/> bound to <paramref name="guards"/> and
    /// <paramref name="stateAccessor"/>. Tools that are not <see cref="AIFunction"/>
    /// instances are passed through unchanged. When <paramref name="guards"/> is empty,
    /// the input sequence is returned as-is.
    /// </summary>
    public static IEnumerable<AITool> WrapTools(
        IEnumerable<AITool> tools,
        IReadOnlyList<IIvrStepGuard> guards,
        Func<IvrWorkflowState> stateAccessor,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(guards);
        ArgumentNullException.ThrowIfNull(stateAccessor);

        if (guards.Count == 0)
        {
            return tools;
        }

        var logger = loggerFactory?.CreateLogger<GuardedAIFunction>();
        return WrapIterator(tools, guards, stateAccessor, logger);
    }

    private static IEnumerable<AITool> WrapIterator(
        IEnumerable<AITool> tools,
        IReadOnlyList<IIvrStepGuard> guards,
        Func<IvrWorkflowState> stateAccessor,
        ILogger<GuardedAIFunction>? logger)
    {
        foreach (var tool in tools)
        {
            yield return tool is AIFunction fn
                ? new GuardedAIFunction(fn, guards, stateAccessor, logger)
                : tool;
        }
    }
}
