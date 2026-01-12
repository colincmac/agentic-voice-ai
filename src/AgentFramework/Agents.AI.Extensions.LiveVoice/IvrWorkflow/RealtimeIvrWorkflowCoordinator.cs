using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.RealtimeVoice;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.Extensions.LiveVoice.IvrWorkflow;

/// <summary>
/// Coordinates a Realtime AI Agent with a Microsoft Agent Framework Workflow for IVR orchestration.
/// This class bridges the gap between the long-running realtime agent stream and the discrete
/// workflow execution model.
/// </summary>
/// <remarks>
/// <para>
/// The Realtime AI Agent runs continuously via <see cref="AuthorizingRealtimeAIAgent.RunStreamingAsync"/>,
/// communicating with a human caller over voice. Meanwhile, this coordinator uses an Agent Framework Workflow
/// to analyze conversation turns and make decisions about step transitions.
/// </para>
/// <para>
/// When the orchestrator workflow decides a step transition should occur, the coordinator updates
/// the realtime agent's session configuration (prompt and tools) to reflect the new step.
/// </para>
/// </remarks>
public sealed class RealtimeIvrWorkflowCoordinator : IAsyncDisposable
{
    private readonly AuthorizingRealtimeAIAgent _realtimeAgent;
    private readonly RealtimeIvrWorkflowDefinition _workflowDefinition;
    private readonly AIAgent _orchestratorAgent;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RealtimeIvrWorkflowCoordinator> _logger;
    private readonly RealtimeIvrWorkflowController _controller;
    private readonly CancellationTokenSource _cts = new();
    private RealtimeIvrStepConfiguration? _initialConfig;
    private Workflow? _orchestratorWorkflow;
    private bool _disposed;

    /// <summary>
    /// Raised when the workflow step changes, providing the new configuration.
    /// </summary>
    public event Func<RealtimeIvrStepConfiguration, CancellationToken, Task>? OnStepChanged;

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

    public RealtimeIvrWorkflowCoordinator(
        AuthorizingRealtimeAIAgent realtimeAgent,
        RealtimeIvrWorkflowDefinition workflowDefinition,
        AIAgent orchestratorAgent,
        ILoggerFactory? loggerFactory = null)
    {
        _realtimeAgent = realtimeAgent;
        _workflowDefinition = workflowDefinition;
        _orchestratorAgent = orchestratorAgent;

        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<RealtimeIvrWorkflowCoordinator>();

        _controller = new RealtimeIvrWorkflowController(
            realtimeAgent,
            workflowDefinition,
            _loggerFactory.CreateLogger<RealtimeIvrWorkflowController>());

        WireUpControllerEvents();
    }

    /// <summary>
    /// Gets the current workflow state.
    /// </summary>
    public IvrWorkflowState WorkflowState => _controller.WorkflowState;

    /// <summary>
    /// Gets the current step ID.
    /// </summary>
    public string? CurrentStepId => _controller.CurrentStepId;

    /// <summary>
    /// Gets the conversation thread associated with the realtime agent.
    /// </summary>
    public ConversationSessionThread? Thread => _controller.Thread;

    /// <summary>
    /// Initializes the coordinator and returns the initial step configuration.
    /// </summary>
    /// <returns>The initial step configuration for the realtime agent.</returns>
    public async Task<RealtimeIvrStepConfiguration> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialConfig is not null) return _initialConfig;
        // Initialize the controller
        _initialConfig = await _controller.InitializeAsync(cancellationToken);

        // Build the orchestrator workflow
        _orchestratorWorkflow = BuildOrchestratorWorkflow();

        _logger.LogInformation(
            "Coordinator initialized for workflow {WorkflowName}",
            _workflowDefinition.Name);

        return _initialConfig;
    }

    /// <summary>
    /// Processes a completed agent turn and determines if any workflow actions are needed.
    /// </summary>
    /// <param name="turn">The completed conversation turn.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A new step configuration if a transition occurred, null otherwise.</returns>
    public async Task<RealtimeIvrStepConfiguration?> ProcessTurnAsync(
        RealtimeVoiceAgentTurn turn,
        CancellationToken cancellationToken = default)
    {
        if (_orchestratorWorkflow is null)
        {
            throw new InvalidOperationException("Coordinator has not been initialized. Call InitializeAsync first.");
        }

        // Add transcript messages to state
        foreach (var message in turn.TranscriptionMessages)
        {
            await _controller.AddTranscriptMessageAsync(message, cancellationToken);
        }

        // Run the orchestrator workflow to analyze the turn
        await using var run = await InProcessExecution.RunAsync(_orchestratorWorkflow, turn, cancellationToken: cancellationToken);
        // Check for orchestrator decision outputs
        RealtimeIvrStepConfiguration? newConfig = null;

        foreach (var evt in run.NewEvents)
        {
            if (evt is WorkflowOutputEvent outputEvent && outputEvent.Data is RealtimeOrchestratorDecision decision)
            {
                newConfig = await ProcessOrchestratorDecisionAsync(decision, cancellationToken);
            }
        }

        return newConfig;
    }

    private async Task<RealtimeIvrStepConfiguration?> ProcessOrchestratorDecisionAsync(
        RealtimeOrchestratorDecision decision,
        CancellationToken cancellationToken)
    {
        // Store extracted data
        if (decision.ExtractedData is { Count: > 0 })
        {
            await _controller.RecordExtractedDataAsync(decision.ExtractedData, cancellationToken);
        }

        // Handle escalation
        if (decision.ShouldEscalate)
        {
            await _controller.RequestEscalationAsync(
                decision.EscalationReason ?? "User requested human assistance",
                cancellationToken);
            return null;
        }

        // Handle workflow completion
        if (decision.ShouldEndWorkflow)
        {
            await _controller.CompleteWorkflowAsync(null, cancellationToken);
            return null;
        }

        // Handle step transition
        if (decision.ShouldTransition && decision.NextStepId is not null)
        {
            return await _controller.TryTransitionToStepAsync(
                decision.NextStepId,
                decision.TransitionReason,
                cancellationToken);
        }

        return null;
    }

    private Workflow BuildOrchestratorWorkflow()
    {
        // Create the orchestrator executor with proper logging
        var orchestratorExecutor = new RealtimeIvrOrchestratorExecutor(
            "ivr-orchestrator",
            _orchestratorAgent,
            _workflowDefinition,
            _loggerFactory.CreateLogger<RealtimeIvrOrchestratorExecutor>());

        // Build a simple single-executor workflow
        // The orchestrator receives turns and outputs decisions
        var workflow = new WorkflowBuilder(orchestratorExecutor)
            .WithOutputFrom(orchestratorExecutor)
            .Build();
        
        return workflow;
    }

    private void WireUpControllerEvents()
    {
        // Forward controller events to coordinator subscribers
        // Only invoke if there are actual subscribers to avoid unnecessary async machinery
        _controller.OnStepChanged += async (stepId, prompt, ct) =>
        {
            if (OnStepChanged is null)
            {
                return;
            }

            var step = _workflowDefinition.GetStep(stepId);
            if (step is not null)
            {
                var config = new RealtimeIvrStepConfiguration
                {
                    StepId = stepId,
                    SystemPrompt = prompt,
                    AvailableTools = step.AvailableTools ?? []
                };
                await OnStepChanged(config, ct).ConfigureAwait(false);
            }
        };

        _controller.OnWorkflowCompleted += (state, message, ct) =>
            OnWorkflowCompleted?.Invoke(state, message, ct) ?? Task.CompletedTask;

        _controller.OnWorkflowFailed += (state, error, ct) =>
            OnWorkflowFailed?.Invoke(state, error, ct) ?? Task.CompletedTask;

        _controller.OnEscalationRequested += (reason, ct) =>
            OnEscalationRequested?.Invoke(reason, ct) ?? Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _cts.CancelAsync();
        await _controller.DisposeAsync();
        _cts.Dispose();
    }
}

/// <summary>
/// Factory for creating IVR workflow coordinators.
/// </summary>
public interface IRealtimeIvrWorkflowCoordinatorFactory
{
    /// <summary>
    /// Creates a new workflow coordinator for the specified session.
    /// </summary>
    RealtimeIvrWorkflowCoordinator Create(
        string sessionId,
        AuthorizingRealtimeAIAgent realtimeAgent,
        RealtimeIvrWorkflowDefinition workflowDefinition);
}

/// <summary>
/// Default implementation of <see cref="IRealtimeIvrWorkflowCoordinatorFactory"/>.
/// </summary>
public sealed class RealtimeIvrWorkflowCoordinatorFactory : IRealtimeIvrWorkflowCoordinatorFactory
{
    private readonly Func<AIAgent> _orchestratorAgentFactory;
    private readonly ILoggerFactory _loggerFactory;

    public RealtimeIvrWorkflowCoordinatorFactory(
        Func<AIAgent> orchestratorAgentFactory,
        ILoggerFactory? loggerFactory = null)
    {
        _orchestratorAgentFactory = orchestratorAgentFactory;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public RealtimeIvrWorkflowCoordinator Create(
        string sessionId,
        AuthorizingRealtimeAIAgent realtimeAgent,
        RealtimeIvrWorkflowDefinition workflowDefinition)
    {
        var orchestratorAgent = _orchestratorAgentFactory();

        return new RealtimeIvrWorkflowCoordinator(
            realtimeAgent,
            workflowDefinition,
            orchestratorAgent,
            _loggerFactory);
    }
}
