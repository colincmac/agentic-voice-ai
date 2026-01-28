using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.RealtimeVoice;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.Extensions.LiveVoice.IvrWorkflow;

/// <summary>
/// Controls a Realtime AI Agent's configuration dynamically based on IVR workflow state.
/// This controller updates the agent's prompt and available tools as the workflow progresses through steps,
/// ensuring the agent only has access to appropriate tools at each stage.
/// </summary>
/// <remarks>
/// Unlike traditional executors that wrap agent invocations, this controller works alongside a running
/// realtime agent stream. The agent runs continuously via <see cref="AuthorizingRealtimeAIAgent.RunStreamingAsync"/>,
/// and this controller updates its session configuration when workflow steps change.
/// </remarks>
public sealed class RealtimeIvrWorkflowController : IAsyncDisposable
{
    private readonly AuthorizingRealtimeAIAgent _agent;
    private readonly RealtimeIvrWorkflowDefinition _workflow;
    private readonly ILogger<RealtimeIvrWorkflowController> _logger;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private readonly RealtimeIvrControllerState _state;
    private LiveConversationAgentSession? _thread;
    private bool _isStarted;
    private bool _disposed;

    /// <summary>
    /// Raised when the workflow step changes, providing the new step ID and system prompt.
    /// </summary>
    public event Func<string, string, CancellationToken, Task>? OnStepChanged;

    /// <summary>
    /// Raised when the workflow completes successfully.
    /// </summary>
    public event Func<IvrWorkflowState, string?, CancellationToken, Task>? OnWorkflowCompleted;

    /// <summary>
    /// Raised when the workflow fails.
    /// </summary>
    public event Func<IvrWorkflowState, string, CancellationToken, Task>? OnWorkflowFailed;

    /// <summary>
    /// Raised when escalation to a human is requested.
    /// </summary>
    public event Func<string, CancellationToken, Task>? OnEscalationRequested;

    public RealtimeIvrWorkflowController(
        AuthorizingRealtimeAIAgent agent,
        RealtimeIvrWorkflowDefinition workflow,
        ILogger<RealtimeIvrWorkflowController>? logger = null)
    {
        _agent = agent;
        _workflow = workflow;
        _logger = logger ?? NullLogger<RealtimeIvrWorkflowController>.Instance;
        _state = new RealtimeIvrControllerState
        {
            WorkflowState = new IvrWorkflowState { Status = IvrWorkflowStatus.NotStarted }
        };
    }

    /// <summary>
    /// Gets the current workflow state.
    /// </summary>
    public IvrWorkflowState WorkflowState => _state.WorkflowState;

    /// <summary>
    /// Gets the current step ID.
    /// </summary>
    public string? CurrentStepId => _state.CurrentStepId;

    /// <summary>
    /// Gets the current system prompt being used.
    /// </summary>
    public string? CurrentPrompt => _state.CurrentPrompt;

    /// <summary>
    /// Gets whether the controller has been started.
    /// </summary>
    public bool IsStarted => _isStarted;

    /// <summary>
    /// Gets the conversation thread associated with this controller.
    /// </summary>
    public LiveConversationAgentSession? Thread => _thread;

    /// <summary>
    /// Initializes the controller and prepares the first step.
    /// Call this before starting the agent stream.
    /// </summary>
    /// <returns>The initial system prompt for the first step.</returns>
    public async Task<RealtimeIvrStepConfiguration> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            if (_isStarted)
            {
                throw new InvalidOperationException("Controller has already been initialized.");
            }

            var initialStepId = _workflow.InitialStepId;
            var step = _workflow.GetStep(initialStepId)
                       ?? throw new InvalidOperationException($"Initial step '{initialStepId}' not found");

            _state.CurrentStepId = initialStepId;
            _state.StepStartedAt = DateTimeOffset.UtcNow;
            _state.WorkflowState.Status = IvrWorkflowStatus.Running;
            _state.WorkflowState.CurrentStepName = initialStepId;
            _state.WorkflowState.CurrentStepIndex = 0;

            // Build the prompt for this step
            _state.CurrentPrompt = _workflow.BuildPromptForStep(initialStepId, _state.WorkflowState);

            // Get the thread (create if needed)
            _thread ??= await _agent.GetNewSessionAsync(cancellationToken);

            _isStarted = true;

            _logger.LogInformation(
                "Initialized workflow {WorkflowName} at step {StepId}",
                _workflow.Name,
                initialStepId);

            return new RealtimeIvrStepConfiguration
            {
                StepId = initialStepId,
                SystemPrompt = _state.CurrentPrompt,
                AvailableTools = step.AvailableTools ?? []
            };
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Attempts to transition to a new step if guards pass.
    /// </summary>
    /// <param name="targetStepId">The step to transition to.</param>
    /// <param name="reason">Optional reason for the transition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new step configuration if transition succeeded, null if guards failed.</returns>
    public async Task<RealtimeIvrStepConfiguration?> TryTransitionToStepAsync(
        string targetStepId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            if (!_isStarted)
            {
                throw new InvalidOperationException("Controller has not been initialized.");
            }

            var currentStep = _state.CurrentStepId is not null
                ? _workflow.GetStep(_state.CurrentStepId)
                : null;

            // Validate transition is allowed
            if (currentStep is not null && !currentStep.ValidTransitions.Contains(targetStepId))
            {
                _logger.LogWarning(
                    "Invalid transition from {CurrentStep} to {TargetStep}",
                    _state.CurrentStepId,
                    targetStepId);
                return null;
            }

            var targetStep = _workflow.GetStep(targetStepId);
            if (targetStep is null)
            {
                _logger.LogWarning("Target step {StepId} not found", targetStepId);
                return null;
            }

            // Evaluate guards
            var guardResult = await EvaluateGuardsAsync(targetStep, _state.WorkflowState, cancellationToken);
            if (!guardResult.Passed)
            {
                _logger.LogWarning(
                    "Guard failed for step {StepId}: {Reason}",
                    targetStepId,
                    guardResult.FailureReason);
                return null;
            }

            // Mark current step as completed
            if (_state.CurrentStepId is not null)
            {
                _state.WorkflowState.MarkStepCompleted(_state.CurrentStepId);
            }

            // Perform transition
            _state.CurrentStepId = targetStepId;
            _state.CurrentStepRetryCount = 0;
            _state.StepStartedAt = DateTimeOffset.UtcNow;
            _state.WorkflowState.CurrentStepName = targetStepId;
            _state.WorkflowState.CurrentStepIndex = _workflow.GetStepIndex(targetStepId);

            // Build new prompt
            _state.CurrentPrompt = _workflow.BuildPromptForStep(targetStepId, _state.WorkflowState);

            _logger.LogInformation(
                "Transitioned to step {StepId} (reason: {Reason})",
                targetStepId,
                reason);

            // Notify listeners
            if (OnStepChanged is not null)
            {
                await OnStepChanged(targetStepId, _state.CurrentPrompt, cancellationToken);
            }

            return new RealtimeIvrStepConfiguration
            {
                StepId = targetStepId,
                SystemPrompt = _state.CurrentPrompt,
                AvailableTools = targetStep.AvailableTools ?? []
            };
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Records extracted data into the workflow state.
    /// </summary>
    public async Task RecordExtractedDataAsync(
        Dictionary<string, object> data,
        CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var (key, value) in data)
            {
                _state.WorkflowState.Set(key, value);
                _logger.LogDebug("Stored extracted data: {Key} = {Value}", key, value);
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }


    /// <summary>
    /// Adds a transcript message to the workflow state.
    /// </summary>
    public async Task AddTranscriptMessageAsync(
        ChatMessage message,
        CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            _state.WorkflowState.AddTranscriptMessage(message);
            _state.WorkflowState.TotalTurns++;
        }
        finally
        {
            _stateLock.Release();
        }
    }


    /// <summary>
    /// Completes the workflow successfully.
    /// </summary>
    public async Task CompleteWorkflowAsync(
        string? completionMessage = null,
        CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            _state.WorkflowState.Status = IvrWorkflowStatus.Completed;

            _logger.LogInformation("Workflow {WorkflowName} completed successfully", _workflow.Name);

            if (OnWorkflowCompleted is not null)
            {
                await OnWorkflowCompleted(_state.WorkflowState, completionMessage, cancellationToken);
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Fails the workflow with an error.
    /// </summary>
    public async Task FailWorkflowAsync(
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            _state.WorkflowState.Status = IvrWorkflowStatus.Failed;
            _state.WorkflowState.ErrorMessage = errorMessage;

            _logger.LogError("Workflow {WorkflowName} failed: {Error}", _workflow.Name, errorMessage);

            if (OnWorkflowFailed is not null)
            {
                await OnWorkflowFailed(_state.WorkflowState, errorMessage, cancellationToken);
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Requests escalation to a human agent.
    /// </summary>
    public async Task RequestEscalationAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            _state.WorkflowState.Status = IvrWorkflowStatus.TransferRequested;

            _logger.LogInformation("Escalation requested: {Reason}", reason);

            if (OnEscalationRequested is not null)
            {
                await OnEscalationRequested(reason, cancellationToken);
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Checks if the current step's requirements are satisfied.
    /// </summary>
    public async Task<bool> ValidateCurrentStepAsync(CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            if (_state.CurrentStepId is null)
            {
                return false;
            }

            var step = _workflow.GetStep(_state.CurrentStepId);
            if (step is null)
            {
                return false;
            }

            return await ValidateStepAsync(step, _state.WorkflowState, cancellationToken);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Gets the current step configuration without transitioning.
    /// </summary>
    public RealtimeIvrStepConfiguration? GetCurrentStepConfiguration()
    {
        if (_state.CurrentStepId is null || _state.CurrentPrompt is null)
        {
            return null;
        }

        var step = _workflow.GetStep(_state.CurrentStepId);

        return new RealtimeIvrStepConfiguration
        {
            StepId = _state.CurrentStepId,
            SystemPrompt = _state.CurrentPrompt,
            AvailableTools = step?.AvailableTools ?? []
        };
    }

    private static async Task<IvrGuardResult> EvaluateGuardsAsync(
        RealtimeIvrWorkflowStep step,
        IvrWorkflowState state,
        CancellationToken cancellationToken)
    {
        // Check authentication level
        if (step.RequiredAuthLevel > state.AuthLevel)
        {
            return IvrGuardResult.Fail(
                $"Insufficient authentication level. Required: {step.RequiredAuthLevel}, Current: {state.AuthLevel}");
        }

        // Evaluate step guards
        foreach (var guard in step.Guards)
        {
            var result = await guard.EvaluateAsync(state, cancellationToken);
            if (!result.Passed)
            {
                return result;
            }
        }

        return IvrGuardResult.Pass();
    }

    private static async Task<bool> ValidateStepAsync(
        RealtimeIvrWorkflowStep step,
        IvrWorkflowState state,
        CancellationToken cancellationToken)
    {
        // Check required state keys
        foreach (var key in step.RequiredStateKeys)
        {
            if (!state.Has(key))
            {
                return false;
            }
        }

        // Evaluate validators
        foreach (var validator in step.Validators)
        {
            var result = await validator.ValidateAsync(state, cancellationToken);
            if (!result.Passed)
            {
                return false;
            }
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stateLock.Dispose();
    }
}

/// <summary>
/// Internal state for the workflow controller.
/// </summary>
internal sealed class RealtimeIvrControllerState
{
    public IvrWorkflowState WorkflowState { get; set; } = new();
    public string? CurrentStepId { get; set; }
    public int CurrentStepRetryCount { get; set; }
    public DateTimeOffset? StepStartedAt { get; set; }
    public string? CurrentPrompt { get; set; }
}

/// <summary>
/// Configuration for a workflow step, including the prompt and available tools.
/// </summary>
public sealed class RealtimeIvrStepConfiguration
{
    /// <summary>
    /// Gets or sets the step ID.
    /// </summary>
    public required string StepId { get; init; }

    /// <summary>
    /// Gets or sets the system prompt for this step.
    /// </summary>
    public required string SystemPrompt { get; init; }

    /// <summary>
    /// Gets or sets the tools available during this step.
    /// </summary>
    public required IReadOnlyList<AITool> AvailableTools { get; init; }
}
