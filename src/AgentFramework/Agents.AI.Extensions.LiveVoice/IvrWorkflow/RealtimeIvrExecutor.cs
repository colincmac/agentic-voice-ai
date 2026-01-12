using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.RealtimeVoice;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.Extensions.LiveVoice.IvrWorkflow;

/// <summary>
/// Represents the state of a Realtime IVR workflow execution.
/// </summary>
public sealed class RealtimeIvrExecutorState
{
    /// <summary>
    /// Gets or sets the current workflow state.
    /// </summary>
    public IvrWorkflowState WorkflowState { get; set; } = new();

    /// <summary>
    /// Gets or sets the current step ID.
    /// </summary>
    public string? CurrentStepId { get; set; }

    /// <summary>
    /// Gets or sets the current step's retry count.
    /// </summary>
    public int CurrentStepRetryCount { get; set; }

    /// <summary>
    /// Gets or sets when the current step started.
    /// </summary>
    public DateTimeOffset? StepStartedAt { get; set; }

    /// <summary>
    /// Gets or sets the current system prompt being used.
    /// </summary>
    public string? CurrentPrompt { get; set; }
}

/// <summary>
/// Workflow executor that manages Realtime AI agent interactions within an IVR workflow.
/// Integrates with the Microsoft Agent Framework Workflow SDK and provides gated tool access
/// based on workflow step progression.
/// </summary>
public sealed class RealtimeIvrExecutor : StatefulExecutor<RealtimeIvrExecutorState>, IResettableExecutor
{
    private readonly AuthorizingRealtimeAIAgent _agent;
    private readonly RealtimeIvrWorkflowDefinition _workflow;
    private readonly ILogger<RealtimeIvrExecutor> _logger;
    private ConversationSessionThread? _thread;

    private static readonly Func<RealtimeIvrExecutorState> initState = () => new();
    private const string ThreadStateKey = "realtime_thread";

    public RealtimeIvrExecutor(
        string id,
        AuthorizingRealtimeAIAgent agent,
        RealtimeIvrWorkflowDefinition workflow,
        ILogger<RealtimeIvrExecutor>? logger = null)
        : base(id, initState, declareCrossRunShareable: true)
    {
        _agent = agent;
        _workflow = workflow;
        _logger = logger ?? NullLogger<RealtimeIvrExecutor>.Instance;
    }

    /// <inheritdoc/>
    protected override RouteBuilder ConfigureRoutes(RouteBuilder routeBuilder)
    {
        return routeBuilder
            .AddHandler<StartWorkflowCommand>(HandleStartWorkflowAsync)
            .AddHandler<ReadOnlyMemory<byte>>(HandleAudioInputAsync)
            .AddHandler<ChatMessage>(HandleChatMessageAsync)
            .AddHandler<RealtimeVoiceAgentTurn>(HandleAgentTurnAsync)
            .AddHandler<TransitionToStepCommand>(HandleStepTransitionAsync)
            .AddHandler<ExtractedDataCommand>(HandleExtractedDataAsync);
    }

    /// <summary>
    /// Starts the workflow and initializes the first step.
    /// </summary>
    private ValueTask HandleStartWorkflowAsync(
        StartWorkflowCommand command,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        return InvokeWithStateAsync(StartWorkflowInternalAsync, context, cancellationToken: cancellationToken);

        async ValueTask<RealtimeIvrExecutorState?> StartWorkflowInternalAsync(
            RealtimeIvrExecutorState? state,
            IWorkflowContext ctx,
            CancellationToken ct)
        {
            state ??= initState();

            var initialStepId = _workflow.InitialStepId;
            var step = _workflow.GetStep(initialStepId)
                       ?? throw new InvalidOperationException($"Initial step '{initialStepId}' not found");

            state.CurrentStepId = initialStepId;
            state.StepStartedAt = DateTimeOffset.UtcNow;
            state.WorkflowState.Status = IvrWorkflowStatus.Running;
            state.WorkflowState.CurrentStepName = initialStepId;
            state.WorkflowState.CurrentStepIndex = 0;

            // Build and apply the prompt for this step
            state.CurrentPrompt = _workflow.BuildPromptForStep(initialStepId, state.WorkflowState);

            // Get thread and configure session with step-specific tools
            var thread = await EnsureThreadAsync(ct);
            await ConfigureSessionForStepAsync(step, state.CurrentPrompt, thread, ct);

            _logger.LogInformation(
                "Started workflow {WorkflowName} at step {StepId}",
                _workflow.Name,
                initialStepId);

            // Emit workflow started event
            await ctx.SendMessageAsync(
                new WorkflowStepChangedEvent(initialStepId, state.CurrentPrompt),
                ct);

            return state;
        }
    }

    /// <summary>
    /// Handles incoming audio frames from the caller.
    /// </summary>
    private async ValueTask HandleAudioInputAsync(
        ReadOnlyMemory<byte> audioFrame,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        var thread = await EnsureThreadAsync(cancellationToken);
        await _agent.SendAudioToRunAsync(new DataContent(audioFrame, "audio/pcm"), thread, cancellationToken);
    }

    /// <summary>
    /// Handles text chat messages (for hybrid scenarios).
    /// </summary>
    private async ValueTask HandleChatMessageAsync(
        ChatMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        var thread = await EnsureThreadAsync(cancellationToken);
        await _agent.SendMessagesToRunAsync([message], thread, cancellationToken);
    }

    /// <summary>
    /// Handles completed agent turns and evaluates step transitions.
    /// </summary>
    private ValueTask HandleAgentTurnAsync(
        RealtimeVoiceAgentTurn turn,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        return InvokeWithStateAsync(ProcessTurnAsync, context, cancellationToken: cancellationToken);

        async ValueTask<RealtimeIvrExecutorState?> ProcessTurnAsync(
            RealtimeIvrExecutorState? state,
            IWorkflowContext ctx,
            CancellationToken ct)
        {
            state ??= initState();

            if (state.CurrentStepId is null)
            {
                _logger.LogWarning("Received agent turn but no current step is set");

                return state;
            }

            var step = _workflow.GetStep(state.CurrentStepId);
            if (step is null)
            {
                _logger.LogWarning("Current step {StepId} not found in workflow", state.CurrentStepId);

                return state;
            }

            // Add transcription to workflow state
            state.WorkflowState.Transcript.AddRange(turn.TranscriptionMessages);
            state.WorkflowState.TotalTurns++;

            // Check step duration timeout
            if (step.MaxDuration.HasValue && state.StepStartedAt.HasValue)
            {
                var elapsed = DateTimeOffset.UtcNow - state.StepStartedAt.Value;
                if (elapsed > step.MaxDuration.Value)
                {
                    _logger.LogWarning(
                        "Step {StepId} exceeded max duration {MaxDuration}",
                        step.Id,
                        step.MaxDuration);

                    await ctx.SendMessageAsync(
                        new StepTimeoutEvent(step.Id, elapsed),
                        ct);
                }
            }

            // Validate step completion
            var validationPassed = await ValidateStepAsync(step, state.WorkflowState, ct);
            if (validationPassed)
            {
                // Execute step completion callback
                if (step.OnCompleted is not null)
                {
                    await step.OnCompleted(state.WorkflowState, ct);
                }

                state.WorkflowState.MarkStepCompleted(step.Id);

                await ctx.SendMessageAsync(
                    new StepCompletedEvent(step.Id, state.WorkflowState),
                    ct);
            }

            return state;
        }
    }

    /// <summary>
    /// Handles explicit step transitions (from orchestrator decisions).
    /// </summary>
    private ValueTask HandleStepTransitionAsync(
        TransitionToStepCommand command,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        return InvokeWithStateAsync(TransitionStepAsync, context, cancellationToken: cancellationToken);

        async ValueTask<RealtimeIvrExecutorState?> TransitionStepAsync(
            RealtimeIvrExecutorState? state,
            IWorkflowContext ctx,
            CancellationToken ct)
        {
            state ??= initState();

            var currentStep = state.CurrentStepId is not null
                ? _workflow.GetStep(state.CurrentStepId)
                : null;

            // Validate transition is allowed
            if (currentStep is not null && !currentStep.ValidTransitions.Contains(command.TargetStepId))
            {
                _logger.LogWarning(
                    "Invalid transition from {CurrentStep} to {TargetStep}",
                    state.CurrentStepId,
                    command.TargetStepId);

                await ctx.SendMessageAsync(
                    new InvalidTransitionEvent(state.CurrentStepId, command.TargetStepId),
                    ct);

                return state;
            }

            var targetStep = _workflow.GetStep(command.TargetStepId);
            if (targetStep is null)
            {
                _logger.LogWarning("Target step {StepId} not found", command.TargetStepId);

                return state;
            }

            // Evaluate guards
            var guardResult = await EvaluateGuardsAsync(targetStep, state.WorkflowState, ct);
            if (!guardResult.Passed)
            {
                _logger.LogWarning(
                    "Guard failed for step {StepId}: {Reason}",
                    command.TargetStepId,
                    guardResult.FailureReason);

                await ctx.SendMessageAsync(
                    new GuardFailedEvent(command.TargetStepId, guardResult.FailureReason),
                    ct);

                return state;
            }

            // Perform transition
            state.CurrentStepId = command.TargetStepId;
            state.CurrentStepRetryCount = 0;
            state.StepStartedAt = DateTimeOffset.UtcNow;
            state.WorkflowState.CurrentStepName = command.TargetStepId;
            state.WorkflowState.CurrentStepIndex = _workflow.GetStepIndex(command.TargetStepId);

            // Build new prompt and reconfigure session
            state.CurrentPrompt = _workflow.BuildPromptForStep(command.TargetStepId, state.WorkflowState);

            var thread = await EnsureThreadAsync(ct);
            await ConfigureSessionForStepAsync(targetStep, state.CurrentPrompt, thread, ct);

            _logger.LogInformation(
                "Transitioned to step {StepId} (reason: {Reason})",
                command.TargetStepId,
                command.Reason);

            await ctx.SendMessageAsync(
                new WorkflowStepChangedEvent(command.TargetStepId, state.CurrentPrompt),
                ct);

            return state;
        }
    }

    /// <summary>
    /// Handles extracted data from the orchestrator.
    /// </summary>
    private ValueTask HandleExtractedDataAsync(
        ExtractedDataCommand command,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        return InvokeWithStateAsync(StoreDataAsync, context, cancellationToken: cancellationToken);

        ValueTask<RealtimeIvrExecutorState?> StoreDataAsync(
            RealtimeIvrExecutorState? state,
            IWorkflowContext ctx,
            CancellationToken ct)
        {
            state ??= initState();

            foreach (var (key, value) in command.Data)
            {
                state.WorkflowState.Set(key, value);
                _logger.LogDebug("Stored extracted data: {Key} = {Value}", key, value);
            }

            return ValueTask.FromResult<RealtimeIvrExecutorState?>(state);
        }
    }

    private async Task<ConversationSessionThread> EnsureThreadAsync(CancellationToken cancellationToken)
    {
        return _thread ??= await _agent.GetNewThreadAsync(cancellationToken);
    }

    private async Task ConfigureSessionForStepAsync(
        RealtimeIvrWorkflowStep step,
        string systemPrompt,
        ConversationSessionThread thread,
        CancellationToken cancellationToken)
    {
        // Get step-specific tools
        var tools = step.AvailableTools ?? [];

        _logger.LogDebug(
            "Configuring session for step {StepId} with {ToolCount} tools",
            step.Id,
            tools.Count);

        // Update the session with new instructions and tools
        // This uses session.update to dynamically change the agent's configuration

        // TODO: UpdateSessionAsync does not exist. Fix
        //await _agent.UpdateSessionAsync(
        //    thread,
        //    new LiveConversationSessionOptions
        //    {
        //        Instructions = systemPrompt,
        //        Tools = [.. tools]
        //    },
        //    cancellationToken);
    }

    private async Task<IvrGuardResult> EvaluateGuardsAsync(
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

    private async Task<bool> ValidateStepAsync(
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

    protected override async ValueTask OnCheckpointRestoredAsync(
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        var threadValue = await context.ReadStateAsync<JsonElement?>(
            ThreadStateKey,
            cancellationToken: cancellationToken);

        if (threadValue.HasValue)
        {
            var thread = _agent.DeserializeThread(threadValue.Value);
            if (thread is ConversationSessionThread sessionThread)
            {
                _thread = sessionThread;
            }
        }

        await base.OnCheckpointRestoredAsync(context, cancellationToken);
    }

    protected override async ValueTask OnCheckpointingAsync(
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        if (_thread is not null)
        {
            var threadValue = _thread.Serialize();
            await context.QueueStateUpdateAsync(
                ThreadStateKey,
                threadValue,
                cancellationToken: cancellationToken);
        }

        await base.OnCheckpointingAsync(context, cancellationToken);
    }

    ValueTask IResettableExecutor.ResetAsync() => ResetAsync();
}

#region Commands and Events

/// <summary>
/// Command to start the workflow.
/// </summary>
public sealed record StartWorkflowCommand;

/// <summary>
/// Command to transition to a specific step.
/// </summary>
public sealed record TransitionToStepCommand(string TargetStepId, string? Reason = null);

/// <summary>
/// Command to store extracted data in workflow state.
/// </summary>
public sealed record ExtractedDataCommand(Dictionary<string, object> Data);

/// <summary>
/// Event raised when the workflow step changes.
/// </summary>
public sealed record WorkflowStepChangedEvent(string StepId, string SystemPrompt);

/// <summary>
/// Event raised when a step completes successfully.
/// </summary>
public sealed record StepCompletedEvent(string StepId, IvrWorkflowState State);

/// <summary>
/// Event raised when a step times out.
/// </summary>
public sealed record StepTimeoutEvent(string StepId, TimeSpan Elapsed);

/// <summary>
/// Event raised when a guard check fails.
/// </summary>
public sealed record GuardFailedEvent(string StepId, string? Reason);

/// <summary>
/// Event raised when an invalid transition is attempted.
/// </summary>
public sealed record InvalidTransitionEvent(string? FromStepId, string ToStepId);

#endregion
