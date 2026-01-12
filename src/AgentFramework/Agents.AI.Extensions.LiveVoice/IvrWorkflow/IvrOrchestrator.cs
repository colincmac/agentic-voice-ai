using System.ComponentModel;
using System.Text.Json;
using Agents.AI.Extensions.Helpers.Streaming;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.Extensions.LiveVoice.IvrWorkflow;


/// <summary>
/// Orchestrator decision output with stage transitions, instructions, extracted data, and transfer decisions.
/// </summary>
public sealed record OrchestratorDecision
{
    /// <summary>
    /// Whether a step transition should occur.
    /// </summary>
    public bool ShouldTransitionStep { get; init; }

    /// <summary>
    /// Target stage to transition to (if ShouldTransitionStep is true).
    /// </summary>
    public string? NextStageId { get; init; }

    /// <summary>
    /// Reason for the stage transition.
    /// </summary>
    public string? TransitionReason { get; init; }

    /// <summary>
    /// Updated instructions for the voice agent.
    /// </summary>
    public string? UpdatedInstructions { get; init; }

    /// <summary>
    /// Data extracted from the conversation.
    /// </summary>
    public Dictionary<string, object>? ExtractedData { get; init; }

    /// <summary>
    /// Whether to request a transfer.
    /// </summary>
    public bool ShouldTransfer { get; init; }

    /// <summary>
    /// Transfer decision details (if ShouldTransfer is true).
    /// </summary>
    //public TransferDecision? Transfer { get; init; }

    /// <summary>
    /// Whether the call should end.
    /// </summary>
    public bool ShouldEndCall { get; init; }

    /// <summary>
    /// Detected customer sentiment (-1.0 to 1.0).
    /// </summary>
    public double Sentiment { get; init; } = 0.0;

    /// <summary>
    /// Detected customer frustration level (0.0 to 1.0).
    /// </summary>
    public double FrustrationLevel { get; init; } = 0.0;

    /// <summary>
    /// Confidence score for this decision (0.0 to 1.0).
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    /// Additional notes or context about the decision.
    /// </summary>
    public string? Notes { get; init; }
}

[Obsolete("IvrStepExecutor is deprecated and will be removed in future releases. Please use the updated Realtime IVR workflow components.")]
public sealed class IvrStepExecutor : StatefulExecutor<IvrWorkflowState>
{
    private readonly ILogger<IvrStepExecutor> _logger;
    private readonly IvrWorkflowDefinition _workflow;
    private readonly ChatClientAgent _agent;
    private readonly JsonSerializerOptions _jsonOptions = LiveVoiceJsonUtilities.DefaultOptions;
    private readonly ChatClientAgentRunOptions _agentRunOptions;
    private readonly ChatOptions _chatOptions;
    private readonly JsonElement _jsonOutputSchema;

    private static readonly JsonElement handoffSchema = AIFunctionFactory.Create(
    ([Description("The reason for the handoff")] string? reasonForHandoff) => { }).JsonSchema;
    private readonly HashSet<string> _handoffFunctionNames = [];


    public IvrStepExecutor(string id, ChatClientAgent agent, IvrWorkflowDefinition workflowDefinition, StatefulExecutorOptions? options = null, bool declareCrossRunShareable = true, ILogger<IvrStepExecutor>? logger = null) : base(id, () => new IvrWorkflowState(), options, declareCrossRunShareable)
    {
        _workflow = workflowDefinition;
        _agent = agent;
        _jsonOutputSchema = AIJsonUtilities.CreateJsonSchema(typeof(OrchestratorDecision));

        _chatOptions = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(_jsonOutputSchema)
        };

        _agentRunOptions = new ChatClientAgentRunOptions()
        {
            ChatOptions = _chatOptions
        };
        _logger = logger ?? NullLogger<IvrStepExecutor>.Instance;
    }


    protected override RouteBuilder ConfigureRoutes(RouteBuilder routeBuilder)
    {
        return routeBuilder
            .AddHandler<RealtimeVoiceAgentTurn>(this.HandleTurnUpdateAsync);
    }

    private async ValueTask FailWorkflow(IWorkflowContext context, IvrWorkflowState state, string errorMessage, CancellationToken cancellationToken)
    {
        state.Status = IvrWorkflowStatus.Failed;
        state.ErrorMessage = errorMessage;
        var message = _workflow.FailureMessage ?? errorMessage;
        await context.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }


    public ValueTask HandleTurnUpdateAsync(RealtimeVoiceAgentTurn update, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        return InvokeWithStateAsync(InvokeTurnUpdateAsync, context, cancellationToken: cancellationToken);

        async ValueTask<IvrWorkflowState?> InvokeTurnUpdateAsync(IvrWorkflowState current, IWorkflowContext context, CancellationToken cancellationToken)
        {
            current.Transcript.AddRange(update.TranscriptionMessages);

            var step = _workflow.Steps[current.CurrentStepIndex];

            var guardResult = await EvaluateGuardsAsync(step, current, cancellationToken).ConfigureAwait(false);

            if (!guardResult.Passed)
            {
                current.Status = IvrWorkflowStatus.Failed;
                current.ErrorMessage = $"Guard failed for first step: {guardResult.FailureReason}";
                _logger.LogError("Workflow {WorkflowName} failed: {Error}", _workflow.Name, _workflow.FailureMessage);

                var failResult = IvrStepResult.Failed(_workflow.FailureMessage ?? current.ErrorMessage);
                await context.SendMessageAsync(failResult, cancellationToken).ConfigureAwait(false);
            }

            var thread = _agent.GetNewThread();
            var messages = new List<ChatMessage>(); // BuildOrchestratorMessages(update, current, step);
            var response = await _agent.RunAsync<OrchestratorDecision>(
                messages: messages,
                thread: thread,
                serializerOptions: _jsonOptions,
                options: _agentRunOptions,
                useJsonSchemaResponseFormat: true,
                cancellationToken: cancellationToken);

            await context.SendMessageAsync(response.Result, cancellationToken).ConfigureAwait(false);

            return current;
        };
        
    }

    //public ValueTask HandleOrchestratorDecisionResultAsync(OrchestratorDecision result, IWorkflowContext context, CancellationToken cancellationToken = default)
    //{
    //    return InvokeWithStateAsync(InvokeOrchestratorDecisionResultAsync, context, cancellationToken: cancellationToken);
    //    async ValueTask<IvrWorkflowState?> InvokeOrchestratorDecisionResultAsync(IvrWorkflowState current, IWorkflowContext context, CancellationToken cancellationToken)
    //    {
    //        var step = _workflow.Steps[current.CurrentStepIndex];
    //        if (result.ShouldTransitionStep)
    //        {
    //            if (result.NextStageId == null || !step.ValidTransitions.Contains(result.NextStageId))
    //            {
    //                await FailWorkflow(context, current, $"Invalid step transition to '{result.NextStageId}' from step '{step.Name}'.", cancellationToken);
    //                return current;
    //            }
    //            current.MarkStepCompleted(step.Name);
    //            current.CurrentStepRetryCount = 0;
    //            current.CurrentStepIndex = _workflow.GetStepIndexByName(result.NextStageId);
    //            if (result.ExtractedData != null)
    //            {
    //                foreach (var kvp in result.ExtractedData)
    //                {
    //                    current.Set(kvp.Key, kvp.Value);
    //                }
    //            }
    //        }
    //        if (result.ShouldEndCall)
    //        {
    //            current.Status = IvrWorkflowStatus.Completed;
    //            await context.SendMessageAsync(_workflow.CompletionMessage ?? "Thank you for calling. Goodbye!", cancellationToken).ConfigureAwait(false);
    //        }
    //        if (result.ShouldTransfer && result.Transfer is not null)
    //        {
    //            current.Status = IvrWorkflowStatus.TransferRequested;
    //            // Handle transfer logic here (e.g., notify system to transfer call)
    //            await context.SendMessageAsync($"Transferring call to {result.Transfer.Destination}.", cancellationToken).ConfigureAwait(false);
    //        }
    //        return current;
    //    }
    //    ;
    //}
    public ValueTask HandleInputResultAsync(IvrStepResult result, IWorkflowContext context, CancellationToken cancellationToken = default)
    {

        return InvokeWithStateAsync(InvokeHandleInputResultAsync, context, cancellationToken: cancellationToken);

        async ValueTask<IvrWorkflowState?> InvokeHandleInputResultAsync(IvrWorkflowState current, IWorkflowContext context, CancellationToken cancellationToken)
        {
            var step = _workflow.Steps[current.CurrentStepIndex];
            if (result.Success)
            {

                current.MarkStepCompleted(step.Name);

                current.CurrentStepRetryCount = 0;
                current.CurrentStepIndex++;
            }

            if (result.ShouldRetry)
            {
                current.CurrentStepRetryCount++;
                if (current.CurrentStepRetryCount > step.MaxRetries)
                {
                    await FailWorkflow(context, current, $"Maximum retries exceeded for step '{step.Name}'.", cancellationToken);
                    return current;
                }
                var retryPrompt = result.Message ?? step.GetRetryPrompt(current.CurrentStepRetryCount, null);
                await context.SendMessageAsync(retryPrompt, cancellationToken).ConfigureAwait(false);
                return current;
            }

            if (!result.Success)
            {
                await FailWorkflow(context, current, result.Message ?? "Step failed.", cancellationToken);
                return current;
            }

            return current;
        }
        ;
    }

    /// <summary>
    /// Builds the prompt for the orchestrator based on the current context.
    /// </summary>
    private string BuildOrchestratorPrompt(
        RealtimeVoiceAgentTurn input,
        IIvrWorkflowStep currentStep,
        IvrWorkflowState state)
    {
        var transcript = string.Join("\n", input.TranscriptionMessages.Select(m =>
            $"{m.AuthorName ?? m.Role.Value}: {m.Text} ({m.CreatedAt:HH:mm:ss})"));

        var stateSnapshot = JsonSerializer.Serialize(state.ToSnapshot(), _jsonOptions);


        var prompt = $@"You are an AI orchestrator for an IVR (Interactive Voice Response) system. Your role is to analyze conversation transcripts and make workflow decisions.

## Current Context

**Current Stage**: {currentStep.Name} ({state.CurrentStepIndex})
**Current User Authentication Level**: {state.AuthLevel}

## Stage Instructions

{currentStep.OrchestratorInstructions}

## Recent Transcript

{transcript}

## Current Workflow State

{stateSnapshot}

## Your Task

Analyze the transcript and workflow state, then provide a JSON decision with the following structure:

{{
  ""shouldTransitionStage"": true/false,
  ""nextStage"": ""stage_id"" or null,
  ""transitionReason"": ""reason for transition"" or null,
  ""updatedInstructions"": ""new instructions for voice agent"" or null,
  ""extractedData"": {{
    ""key"": ""value"",
    ...
  }} or null,
  ""shouldTransfer"": true/false,
  ""transfer"": {{
    ""transferType"": ""human"",
    ""destination"": ""customer_service"",
    ""reason"": ""reason"",
    ""priority"": 0
  }} or null,
  ""shouldEndCall"": true/false,
  ""sentiment"": -1.0 to 1.0,
  ""frustrationLevel"": 0.0 to 1.0,
  ""confidence"": 0.0 to 1.0,
  ""notes"": ""additional context""
}}

**Important**:
- Only transition to stages listed in ""Valid Next Stages""
- Ensure required authentication level is met before transitioning to protected stages
- Extract all relevant data mentioned in the conversation
- Detect sentiment and frustration levels accurately
- Provide clear reasoning for any stage transitions or transfers

Respond with ONLY the JSON decision, no additional text.";

        return prompt;
    }


    //private IEnumerable<ChatMessage> BuildOrchestratorMessages(
    //    RealtimeVoiceAgentTurn input,
    //    IvrWorkflowState state,
    //    IIvrWorkflowStep currentStage)
    //{
    //    // Build context message
    //    var contextJson = JsonSerializer.Serialize(state.ToSnapshot(), _jsonOptions);

    //    yield return new ChatMessage(ChatRole.System, $"""
    //        Current Context:
    //        {contextJson}
            
    //        Stage-specific instructions:
    //        {currentStage.OrchestratorInstructions}
    //        """);

    //    // Add transcript as user message
    //    var transcriptText = string.Join("\n", input.RecentTranscript.Select(t =>
    //        $"[{t.Timestamp:HH:mm:ss}] {t.Speaker}: {t.Text}"));

    //    yield return new ChatMessage(ChatRole.User, $"""
    //        Recent transcript to analyze:
            
    //        {transcriptText}
            
    //        Based on this transcript and the current stage rules, what is your decision?
    //        Respond with a JSON object. 
    //        """);
    //}

    private async Task<IvrGuardResult> EvaluateGuardsAsync(IIvrWorkflowStep step, IvrWorkflowState state, CancellationToken cancellationToken)
    {
        foreach (var guard in step.Guards)
        {
            var result = await guard.EvaluateAsync(state, cancellationToken).ConfigureAwait(false);
            if (!result.Passed)
            {
                _logger.LogWarning("Guard failed for step {StepName}: {Reason}", step.Name, result.FailureReason);
                return result;
            }
        }

        return IvrGuardResult.Pass();
    }
}
