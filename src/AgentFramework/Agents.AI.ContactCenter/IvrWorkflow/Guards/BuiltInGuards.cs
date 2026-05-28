using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Agents.AI.ContactCenter.Authentication;

namespace Agents.AI.ContactCenter.IvrWorkflow.Guards;

/// <summary>
/// Guard that requires the caller to have reached at least a specified
/// <see cref="CallerVerificationLevel"/>.
/// </summary>
public sealed class RequiredAuthLevelGuard : IIvrStepGuard
{
    private readonly string _failureMessage;

    public RequiredAuthLevelGuard(CallerVerificationLevel required, string? failureMessage = null)
    {
        RequiredLevel = required;
        _failureMessage = failureMessage ?? $"Verification level {required} is required.";
    }

    /// <summary>The minimum <see cref="CallerVerificationLevel"/> this guard demands.</summary>
    public CallerVerificationLevel RequiredLevel { get; }

    public Task<IvrGuardResult> EvaluateAsync(IvrWorkflowState state, CancellationToken cancellationToken = default)
    {
        var result = state.VerificationLevel >= RequiredLevel
            ? IvrGuardResult.Pass()
            : IvrGuardResult.Fail(_failureMessage);
        return Task.FromResult(result);
    }
}

/// <summary>
/// Guard that requires every key in <see cref="Keys"/> to be present in workflow state.
/// </summary>
public sealed class RequiredStateKeysGuard : IIvrStepGuard
{
    public RequiredStateKeysGuard(IReadOnlyList<string> keys, string? failureMessage = null)
    {
        Keys = keys ?? throw new ArgumentNullException(nameof(keys));
        FailureMessage = failureMessage;
    }

    public IReadOnlyList<string> Keys { get; }

    public string? FailureMessage { get; }

    public Task<IvrGuardResult> EvaluateAsync(IvrWorkflowState state, CancellationToken cancellationToken = default)
    {
        var missing = new List<string>();
        foreach (var key in Keys)
        {
            if (!state.Has(key))
            {
                missing.Add(key);
            }
        }

        if (missing.Count == 0)
        {
            return Task.FromResult(IvrGuardResult.Pass());
        }

        var message = FailureMessage ?? $"Missing required state keys: {string.Join(", ", missing)}.";
        return Task.FromResult(IvrGuardResult.Fail(message));
    }
}
