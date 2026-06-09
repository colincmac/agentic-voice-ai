using Agents.AI.ContactCenter.Authentication;

namespace Agents.AI.ContactCenter.IvrWorkflow.Predicates;

/// <summary>
/// Factory methods for the predicates the workflow compiler emits from YAML
/// <c>requires:</c> entries. Each factory captures its operands and returns a closure
/// matching the <see cref="EdgePredicate"/> delegate.
/// </summary>
public static class BuiltInPredicates
{
    /// <summary>Always allow.</summary>
    public static EdgePredicate Always() =>
        (_, _) => ValueTask.FromResult(EdgePredicateResult.Allow());

    /// <summary>Always deny with the supplied reason.</summary>
    public static EdgePredicate Never(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return (_, _) => ValueTask.FromResult(EdgePredicateResult.Deny(reason));
    }

    /// <summary>
    /// Allow only when the per-call <see cref="CallerAuthenticationState.Identity"/>'s
    /// <see cref="CallerVerificationLevel"/> is greater than or equal to
    /// <paramref name="minimumLevel"/>. When no <see cref="CallerAuthenticationState"/>
    /// is present on the context, deny (fail closed).
    /// </summary>
    public static EdgePredicate AuthVerificationLevel(
        CallerVerificationLevel minimumLevel,
        string? failureMessage = null) =>
        (ctx, _) =>
        {
            var state = ctx.CallerAuthentication;
            if (state is null)
            {
                return ValueTask.FromResult(EdgePredicateResult.Deny(
                    failureMessage ?? $"Caller authentication is not configured; required level was '{minimumLevel}'."));
            }

            var current = state.Identity.VerificationLevel;
            return ValueTask.FromResult(current >= minimumLevel
                ? EdgePredicateResult.Allow()
                : EdgePredicateResult.Deny(
                    failureMessage ?? $"Caller verification level '{current}' is below required '{minimumLevel}'."));
        };

    /// <summary>Allow when the per-call <see cref="IvrWorkflowState"/> has a non-null value under <paramref name="key"/>.</summary>
    public static EdgePredicate StateHas(string key, string? failureMessage = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return (ctx, _) => ValueTask.FromResult(ctx.WorkflowState.Has(key)
            ? EdgePredicateResult.Allow()
            : EdgePredicateResult.Deny(failureMessage ?? $"Workflow state is missing required key '{key}'."));
    }

    /// <summary>
    /// Allow when the value under <paramref name="key"/> equals <paramref name="expected"/>.
    /// Uses <see cref="object.Equals(object?, object?)"/> for the comparison.
    /// </summary>
    public static EdgePredicate StateEquals(string key, object? expected, string? failureMessage = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return (ctx, _) =>
        {
            var actual = ctx.WorkflowState.Get<object>(key);
            return ValueTask.FromResult(Equals(actual, expected)
                ? EdgePredicateResult.Allow()
                : EdgePredicateResult.Deny(
                    failureMessage ?? $"Workflow state '{key}' = '{actual ?? "<null>"}', expected '{expected ?? "<null>"}'."));
        };
    }

    /// <summary>Combine multiple predicates with logical AND. Short-circuits on the first denial.</summary>
    public static EdgePredicate All(params EdgePredicate[] predicates)
    {
        ArgumentNullException.ThrowIfNull(predicates);
        return async (ctx, ct) =>
        {
            for (var i = 0; i < predicates.Length; i++)
            {
                var result = await predicates[i](ctx, ct).ConfigureAwait(false);
                if (!result.Passed)
                {
                    return result;
                }
            }
            return EdgePredicateResult.Allow();
        };
    }

    /// <summary>Combine multiple predicates with logical OR. Short-circuits on the first allowance. Returns the last denial reason when none pass.</summary>
    public static EdgePredicate Any(params EdgePredicate[] predicates)
    {
        ArgumentNullException.ThrowIfNull(predicates);
        if (predicates.Length == 0)
        {
            return Never("Any() called with no predicates; nothing can match.");
        }
        return async (ctx, ct) =>
        {
            EdgePredicateResult last = EdgePredicateResult.Deny("No predicate matched.");
            for (var i = 0; i < predicates.Length; i++)
            {
                last = await predicates[i](ctx, ct).ConfigureAwait(false);
                if (last.Passed)
                {
                    return last;
                }
            }
            return last;
        };
    }

    /// <summary>Negate a predicate. <paramref name="onAllow"/> becomes the denial reason when the inner predicate passes.</summary>
    public static EdgePredicate Not(EdgePredicate predicate, string onAllow = "Negated predicate matched.")
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return async (ctx, ct) =>
        {
            var result = await predicate(ctx, ct).ConfigureAwait(false);
            return result.Passed
                ? EdgePredicateResult.Deny(onAllow)
                : EdgePredicateResult.Allow();
        };
    }
}
