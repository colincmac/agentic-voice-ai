namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// Guard that requires a specific state key to have a value.
/// </summary>
public sealed class RequiredStateGuard : IIvrStepGuard
{
    private readonly string _stateKey;
    private readonly string _failureMessage;

    public RequiredStateGuard(string stateKey, string? failureMessage = null)
    {
        _stateKey = stateKey;
        _failureMessage = failureMessage ?? $"Required state '{stateKey}' is missing.";
    }

    public Task<IvrGuardResult> EvaluateAsync(IvrWorkflowState state, CancellationToken cancellationToken = default)
    {
        var result = state.Has(_stateKey)
            ? IvrGuardResult.Pass()
            : IvrGuardResult.Fail(_failureMessage);

        return Task.FromResult(result);
    }
}

/// <summary>
/// Guard that requires a previous step to be completed.
/// </summary>
public sealed class PreviousStepCompletedGuard : IIvrStepGuard
{
    private readonly string _stepName;
    private readonly string _failureMessage;

    public PreviousStepCompletedGuard(string stepName, string? failureMessage = null)
    {
        _stepName = stepName;
        _failureMessage = failureMessage ?? $"Step '{stepName}' must be completed first.";
    }

    public Task<IvrGuardResult> EvaluateAsync(IvrWorkflowState state, CancellationToken cancellationToken = default)
    {
        var result = state.IsStepCompleted(_stepName)
            ? IvrGuardResult.Pass()
            : IvrGuardResult.Fail(_failureMessage);

        return Task.FromResult(result);
    }
}

/// <summary>
/// Guard that uses a custom predicate function.
/// </summary>
public sealed class PredicateGuard : IIvrStepGuard
{
    private readonly Func<IvrWorkflowState, bool> _predicate;
    private readonly string _failureMessage;

    public PredicateGuard(Func<IvrWorkflowState, bool> predicate, string failureMessage)
    {
        _predicate = predicate;
        _failureMessage = failureMessage;
    }

    public Task<IvrGuardResult> EvaluateAsync(IvrWorkflowState state, CancellationToken cancellationToken = default)
    {
        var result = _predicate(state)
            ? IvrGuardResult.Pass()
            : IvrGuardResult.Fail(_failureMessage);

        return Task.FromResult(result);
    }
}

/// <summary>
/// Guard that uses an async predicate function.
/// </summary>
public sealed class AsyncPredicateGuard : IIvrStepGuard
{
    private readonly Func<IvrWorkflowState, CancellationToken, Task<bool>> _predicate;
    private readonly string _failureMessage;

    public AsyncPredicateGuard(Func<IvrWorkflowState, CancellationToken, Task<bool>> predicate, string failureMessage)
    {
        _predicate = predicate;
        _failureMessage = failureMessage;
    }

    public async Task<IvrGuardResult> EvaluateAsync(IvrWorkflowState state, CancellationToken cancellationToken = default)
    {
        var passed = await _predicate(state, cancellationToken).ConfigureAwait(false);
        return passed
            ? IvrGuardResult.Pass()
            : IvrGuardResult.Fail(_failureMessage);
    }
}

/// <summary>
/// Validator that checks if a state key has a non-empty string value.
/// </summary>
public sealed class NonEmptyStringValidator : IIvrStepValidator
{
    private readonly string _stateKey;
    private readonly string _failureMessage;

    public NonEmptyStringValidator(string stateKey, string? failureMessage = null)
    {
        _stateKey = stateKey;
        _failureMessage = failureMessage ?? $"Please provide a valid value for {stateKey}.";
    }

    public Task<IvrGuardResult> ValidateAsync(IvrWorkflowState state, CancellationToken cancellationToken = default)
    {
        var value = state.Get<string>(_stateKey);
        var result = !string.IsNullOrWhiteSpace(value)
            ? IvrGuardResult.Pass()
            : IvrGuardResult.Fail(_failureMessage);

        return Task.FromResult(result);
    }
}

/// <summary>
/// Validator that checks a string value matches a pattern.
/// </summary>
public sealed class PatternValidator : IIvrStepValidator
{
    private readonly string _stateKey;
    private readonly string _pattern;
    private readonly string _failureMessage;

    public PatternValidator(string stateKey, string pattern, string failureMessage)
    {
        _stateKey = stateKey;
        _pattern = pattern;
        _failureMessage = failureMessage;
    }

    public Task<IvrGuardResult> ValidateAsync(IvrWorkflowState state, CancellationToken cancellationToken = default)
    {
        var value = state.Get<string>(_stateKey);
        if (string.IsNullOrEmpty(value))
        {
            return Task.FromResult(IvrGuardResult.Fail(_failureMessage));
        }

        var regex = new System.Text.RegularExpressions.Regex(_pattern);
        var result = regex.IsMatch(value)
            ? IvrGuardResult.Pass()
            : IvrGuardResult.Fail(_failureMessage);

        return Task.FromResult(result);
    }
}

/// <summary>
/// Validator that uses a custom predicate function.
/// </summary>
public sealed class PredicateValidator : IIvrStepValidator
{
    private readonly Func<IvrWorkflowState, bool> _predicate;
    private readonly string _failureMessage;

    public PredicateValidator(Func<IvrWorkflowState, bool> predicate, string failureMessage)
    {
        _predicate = predicate;
        _failureMessage = failureMessage;
    }

    public Task<IvrGuardResult> ValidateAsync(IvrWorkflowState state, CancellationToken cancellationToken = default)
    {
        var result = _predicate(state)
            ? IvrGuardResult.Pass()
            : IvrGuardResult.Fail(_failureMessage);

        return Task.FromResult(result);
    }
}

/// <summary>
/// Validator that uses an async validation function.
/// </summary>
public sealed class AsyncValidator : IIvrStepValidator
{
    private readonly Func<IvrWorkflowState, CancellationToken, Task<IvrGuardResult>> _validatorFunc;

    public AsyncValidator(Func<IvrWorkflowState, CancellationToken, Task<IvrGuardResult>> validatorFunc)
    {
        _validatorFunc = validatorFunc;
    }

    public Task<IvrGuardResult> ValidateAsync(IvrWorkflowState state, CancellationToken cancellationToken = default)
    {
        return _validatorFunc(state, cancellationToken);
    }
}
