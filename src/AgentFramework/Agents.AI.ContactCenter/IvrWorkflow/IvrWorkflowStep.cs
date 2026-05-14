namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// Represents the result of a workflow step execution.
/// </summary>
public sealed class IvrStepResult
{
    private IvrStepResult(bool success, bool shouldRetry, string? message, bool skipToStep, string? targetStepName)
    {
        Success = success;
        ShouldRetry = shouldRetry;
        Message = message;
        SkipToStep = skipToStep;
        TargetStepName = targetStepName;
    }

    /// <summary>
    /// Gets whether the step completed successfully.
    /// </summary>
    public bool Success { get; }

    /// <summary>
    /// Gets whether the step should be retried.
    /// </summary>
    public bool ShouldRetry { get; }

    /// <summary>
    /// Gets an optional message (prompt or error).
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// Gets whether to skip to a specific step.
    /// </summary>
    public bool SkipToStep { get; }

    /// <summary>
    /// Gets the name of the step to skip to.
    /// </summary>
    public string? TargetStepName { get; }

    /// <summary>
    /// Creates a successful step result.
    /// </summary>
    public static IvrStepResult Succeeded(string? message = null)
        => new(success: true, shouldRetry: false, message: message, skipToStep: false, targetStepName: null);

    /// <summary>
    /// Creates a failed step result with retry.
    /// </summary>
    public static IvrStepResult RetryWithPrompt(string prompt)
        => new(success: false, shouldRetry: true, message: prompt, skipToStep: false, targetStepName: null);

    /// <summary>
    /// Creates a failed step result without retry.
    /// </summary>
    public static IvrStepResult Failed(string errorMessage)
        => new(success: false, shouldRetry: false, message: errorMessage, skipToStep: false, targetStepName: null);

    /// <summary>
    /// Creates a result that skips to a named step.
    /// </summary>
    public static IvrStepResult JumpToStep(string stepName)
        => new(success: true, shouldRetry: false, message: null, skipToStep: true, targetStepName: stepName);
}

/// <summary>
/// Represents the result of a step guard evaluation.
/// </summary>
public sealed class IvrGuardResult
{
    private IvrGuardResult(bool passed, string? failureReason)
    {
        Passed = passed;
        FailureReason = failureReason;
    }

    /// <summary>
    /// Gets whether the guard check passed.
    /// </summary>
    public bool Passed { get; }

    /// <summary>
    /// Gets the reason for failure, if any.
    /// </summary>
    public string? FailureReason { get; }

    /// <summary>
    /// Creates a passed guard result.
    /// </summary>
    public static IvrGuardResult Pass() => new(passed: true, failureReason: null);

    /// <summary>
    /// Creates a failed guard result.
    /// </summary>
    public static IvrGuardResult Fail(string reason) => new(passed: false, failureReason: reason);
}

/// <summary>
/// Defines a guard that must pass before a step can execute.
/// </summary>
public interface IIvrStepGuard
{
    /// <summary>
    /// Evaluates whether the guard condition is satisfied.
    /// </summary>
    Task<IvrGuardResult> EvaluateAsync(IvrWorkflowState state, CancellationToken cancellationToken = default);
}

/// <summary>
/// Defines a validation function for step state.
/// </summary>
public interface IIvrStepValidator
{
    /// <summary>
    /// Validates the step's state requirements.
    /// </summary>
    Task<IvrGuardResult> ValidateAsync(IvrWorkflowState state, CancellationToken cancellationToken = default);
}

/// <summary>
/// Defines a workflow step that can be executed.
/// </summary>
public interface IIvrWorkflowStep
{
    /// <summary>
    /// Gets the unique name of this step.
    /// </summary>
    string Name { get; }


    /// <summary>
    /// Instructions for the voice agent when in this stage.
    /// </summary>
    string VoiceAgentInstructions { get; }

    /// <summary>
    /// Instructions for the orchestrator on how to evaluate this stage.
    /// </summary>
    string OrchestratorInstructions { get; }

    /// <summary>
    /// Maximum time to spend in this stage before escalating.
    /// </summary>
    TimeSpan? MaxDuration { get; }


    /// <summary>
    /// Whether authentication is required for this stage.
    /// </summary>
    AuthenticationLevel RequiredAuthLevel { get; }

    /// <summary>
    /// Gets the prompt to show if this step fails.
    /// </summary>
    string DefaultFailureResponse { get; }

    /// <summary>
    /// Gets the guards that must pass before this step can execute.
    /// </summary>
    IReadOnlyList<IIvrStepGuard> Guards { get; }

    /// <summary>
    /// Gets the validators that check if this step's requirements are satisfied.
    /// </summary>
    IReadOnlyList<IIvrStepValidator> Validators { get; }

    /// <summary>
    /// Gets the maximum number of retries allowed for this step.
    /// </summary>
    int MaxRetries { get; }

    /// <summary>
    /// Gets the prompt to show when the step needs to be retried.
    /// </summary>
    string GetRetryPrompt(int retryCount, string? lastError);

}

/// <summary>
/// Base implementation of an IVR workflow step.
/// </summary>
public abstract class IvrWorkflowStepBase : IIvrWorkflowStep
{
    private readonly List<IIvrStepGuard> _guards = [];
    private readonly List<IIvrStepValidator> _validators = [];

    /// <inheritdoc />
    public string Name { get; }
    /// <summary>
    /// Instructions for the voice agent when in this stage.
    /// </summary>
    public string VoiceAgentInstructions { get; }

    /// <summary>
    /// Instructions for the orchestrator on how to evaluate this stage.
    /// </summary>
    public string OrchestratorInstructions { get; }

    /// <inheritdoc />
    public virtual string DefaultFailureResponse { get; init; } = "I'm sorry, I couldn't process your request.";

    /// <inheritdoc />
    public IReadOnlyList<IIvrStepGuard> Guards => _guards.AsReadOnly();

    /// <inheritdoc />
    public IReadOnlyList<IIvrStepValidator> Validators => _validators.AsReadOnly();

    /// <inheritdoc />
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Maximum time to spend in this stage before escalating.
    /// </summary>
    public TimeSpan? MaxDuration { get; init; }


    /// <summary>
    /// Gets or sets the retry prompt template. Use {retryCount} and {error} placeholders.
    /// </summary>
    public string RetryPromptTemplate { get; init; } = "I didn't catch that. Let's try again. {error}";

    /// <summary>
    /// Whether authentication is required for this stage.
    /// </summary>
    public AuthenticationLevel RequiredAuthLevel { get; init; } = AuthenticationLevel.None;

    protected IvrWorkflowStepBase(string name, string voiceAgentInstructions, string orchestratorInstructions)
    {
        Name = name;
        VoiceAgentInstructions = voiceAgentInstructions;
        OrchestratorInstructions = orchestratorInstructions;
    }

    /// <summary>
    /// Adds a guard to this step.
    /// </summary>
    public void AddGuard(IIvrStepGuard guard) => _guards.Add(guard);

    /// <summary>
    /// Adds a validator to this step.
    /// </summary>
    public void AddValidator(IIvrStepValidator validator) => _validators.Add(validator);

    /// <inheritdoc />
    public abstract Task<IvrStepResult> ExecuteAsync(
        IvrWorkflowState state,
        string? userInput,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public virtual string GetRetryPrompt(int retryCount, string? lastError)
    {
        return RetryPromptTemplate
            .Replace("{retryCount}", retryCount.ToString())
            .Replace("{error}", lastError ?? string.Empty);
    }

}
