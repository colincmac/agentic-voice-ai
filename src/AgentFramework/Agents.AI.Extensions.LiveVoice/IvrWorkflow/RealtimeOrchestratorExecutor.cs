//using System.ComponentModel;
//using System.Text;
//using System.Text.Json;
//using DnsClient.Internal;
//using Microsoft.Agents.AI;
//using Microsoft.Agents.AI.Workflows;
//using Microsoft.Extensions.AI;
//using Microsoft.Extensions.Logging;
//using Microsoft.Extensions.Logging.Abstractions;
//using Microsoft.Graph.Models.IdentityGovernance;

//namespace Agents.AI.Extensions.LiveVoice.IvrWorkflow;

///// <summary>
///// Orchestrator decision output for Realtime IVR workflows.
///// </summary>
//[Description("Decision output from the orchestrator analyzing a voice conversation turn. Determines workflow progression, data extraction, and escalation needs.")]
//public sealed record RealtimeOrchestratorDecision
//{
//    /// <summary>
//    /// Whether a step transition should occur.
//    /// </summary>
//    [Description("Set to true if the conversation indicates the current step's exit condition has been met and a transition to another step should occur.")]
//    public bool ShouldTransition { get; init; }

//    /// <summary>
//    /// Target step ID to transition to.
//    /// </summary>
//    [Description("The ID of the next workflow step to transition to. Must match one of the valid transition targets defined for the current step. Required when ShouldTransition is true.")]
//    public string? NextStepId { get; init; }

//    /// <summary>
//    /// Reason for the transition.
//    /// </summary>
//    [Description("A brief explanation of why this transition is being recommended, based on the conversation analysis.")]
//    public string? TransitionReason { get; init; }

//    /// <summary>
//    /// Data extracted from the conversation.
//    /// </summary>
//    [Description("Key-value pairs of data extracted from the user's responses during this turn. Keys should match the required state keys defined for the current step.")]
//    public Dictionary<string, object>? ExtractedData { get; init; }

//    /// <summary>
//    /// Whether the workflow should end.
//    /// </summary>
//    [Description("Set to true if the conversation indicates the entire workflow should complete, such as when the user's request has been fully resolved.")]
//    public bool ShouldEndWorkflow { get; init; }

//    /// <summary>
//    /// Whether to escalate to a human.
//    /// </summary>
//    [Description("Set to true if the user explicitly requests human assistance, expresses significant frustration, or the situation requires human intervention.")]
//    public bool ShouldEscalate { get; init; }

//    /// <summary>
//    /// Reason for escalation.
//    /// </summary>
//    [Description("A brief explanation of why escalation to a human agent is being recommended. Required when ShouldEscalate is true.")]
//    public string? EscalationReason { get; init; }

//    /// <summary>
//    /// Detected sentiment score (-1.0 to 1.0).
//    /// </summary>
//    [Description("The detected emotional sentiment of the user in this turn. Range: -1.0 (very negative/frustrated) to 1.0 (very positive/satisfied). Use 0.0 for neutral.")]
//    public double Sentiment { get; init; }

//    /// <summary>
//    /// Confidence score for this decision.
//    /// </summary>
//    [Description("How confident the orchestrator is in this decision. Range: 0.0 (no confidence) to 1.0 (fully confident). Default is 1.0.")]
//    public double Confidence { get; init; } = 1.0;
//}

////public sealed class RealtimeIvrStepExecutor : StatefulExecutor<IvrWorkflowState>
////{
////    private readonly RealtimeIvrWorkflowStep _step;
////    private readonly ILogger<RealtimeIvrStepExecutor> _logger;
////    public RealtimeIvrStepExecutor(RealtimeIvrWorkflowStep stepDefinition, ILogger<RealtimeIvrStepExecutor>? logger = null) : base($"step_executor_{stepDefinition.Id}", () => new IvrWorkflowState())
////    {
////        _step = stepDefinition;
////        _logger = logger ?? NullLogger<RealtimeIvrStepExecutor>.Instance;
////    }

////    protected override RouteBuilder ConfigureRoutes(RouteBuilder routeBuilder)
////    {
////        return routeBuilder
////            .AddHandler<TransitionAction>(HandleTransitionActionAsync);
////    }

////    public async ValueTask HandleTransitionActionAsync(
////        TransitionAction action,
////        IWorkflowContext context,
////        CancellationToken cancellationToken)
////    {
////        await InvokeWithStateAsync(ApplyTransitionAsync, context, cancellationToken: cancellationToken);
////        async ValueTask<IvrWorkflowState?> ApplyTransitionAsync(
////                IvrWorkflowState state,
////                IWorkflowContext context,
////                CancellationToken cancellationToken)
////        {
////            var currentStep = state.CurrentStepName is not null
////                ? _workflow.GetStep(state.CurrentStepName)
////                : _workflow.Steps[0];


////            if (currentStep is null)
////            {
////                _logger.LogWarning("No current step found for transition action");
////                return state;
////            }
////            if (action.NextStepId is not null &&
////                _workflow.GetStep(action.NextStepId) is { } targetStep)
////            {
////                state.CurrentStepName = targetStep.Id;
////                state.CurrentStepIndex = _workflow.Steps.IndexOf(targetStep);
////                state.CurrentStepRetryCount = 0;
////                _logger.LogInformation(
////                    "Transitioned to step {StepId} due to action",
////                    targetStep.Id);
////            }
////            else
////            {
////                _logger.LogWarning(
////                    "Invalid target step ID {TargetStepId} in transition action",
////                    action.TargetStepId);
////            }
////            return state;
////        }
////    }
////}

///// <summary>
///// Executor that orchestrates step transitions by analyzing conversation transcripts
///// and making decisions about workflow progression.
///// </summary>
//public sealed class RealtimeIvrOrchestratorExecutor : StatefulExecutor<IvrWorkflowState>
//{
//    private readonly ChatClientAgent _orchestratorAgent;
//    private readonly RealtimeIvrWorkflowDefinition _workflow;
//    private readonly ILogger<RealtimeIvrOrchestratorExecutor> _logger;
//    private readonly ChatOptions _chatOptions;
//    private readonly JsonSerializerOptions _jsonOptions = LiveVoiceJsonUtilities.DefaultOptions;
//    private readonly Func<IvrWorkflowState> _initializeState;

//    //private ChatClientAgentRunOptions? _agentRunOptions;
//    internal const string FunctionPrefix = "transition_to_";

//    private static readonly JsonElement handoffSchema = AIFunctionFactory.Create(
//        ([Description("The reason for the handoff")] string? reasonForHandoff) => { }).JsonSchema;

//    private readonly HashSet<string> _handoffFunctionNames = [];

//    /// <summary>
//    /// Maximum number of transcript messages to include in context.
//    /// </summary>
//    private const int MaxTranscriptMessages = 15;

//    /// <summary>
//    /// Approximate token limit for conversation context to avoid exceeding model limits.
//    /// </summary>
//    private const int MaxContextTokenEstimate = 2000;

//    /// <summary>
//    /// Static orchestrator role preamble (cached to avoid reconstruction).
//    /// </summary>
//    private const string OrchestratorRolePreamble = """
//        # Role
//        You are a workflow orchestrator analyzing voice conversations to determine step transitions.
//        Your job is to observe the conversation and decide when the current step's goals have been met.

//        # Decision Guidelines
//        - Only recommend transitions when exit conditions are clearly satisfied
//        - Extract data accurately from user responses
//        - Flag escalation only for explicit requests or significant frustration
//        - Be conservative with transitions - prefer staying in current step if uncertain
//        """;

//    public RealtimeIvrOrchestratorExecutor(
//            string id,
//            AIAgent orchestratorAgent,
//            RealtimeIvrWorkflowDefinition workflow,
//            ILogger<RealtimeIvrOrchestratorExecutor>? logger = null
//        ) : base(id, () => new IvrWorkflowState(), declareCrossRunShareable: true)
//    {
//        if (orchestratorAgent is not ChatClientAgent chatClientAgent)
//        {
//            throw new ArgumentException("Orchestrator agent must be a ChatClientAgent", nameof(orchestratorAgent));
//        }

//        _initializeState = () => new IvrWorkflowState()
//        {
//            Status = IvrWorkflowStatus.NotStarted,
//            CurrentStepName = workflow.GetStep(workflow.InitialStepId)?.Id,
//        };


//        _orchestratorAgent = chatClientAgent;
//        _workflow = workflow;
//        _logger = logger ?? NullLogger<RealtimeIvrOrchestratorExecutor>.Instance;

//        var jsonSchema = AIJsonUtilities.CreateJsonSchema(typeof(RealtimeOrchestratorDecision));
//        _chatOptions = new ChatOptions
//        {
//            ResponseFormat = ChatResponseFormat.ForJsonSchema(jsonSchema)
//        };
//    }

//    /// <inheritdoc/>
//    protected override RouteBuilder ConfigureRoutes(RouteBuilder routeBuilder)
//    {
//        return routeBuilder
//            .AddHandler<RealtimeConversationUtterance>(HandleUtteranceAsync)
//            .AddHandler<RealtimeVoiceAgentTurn>(HandleAgentTurnFinishedAsync);
//    }

//    public async ValueTask HandleUtteranceAsync(
//        RealtimeConversationUtterance utterance,
//        IWorkflowContext context,
//        CancellationToken cancellationToken)
//    {
//        await InvokeWithStateAsync(HandleUtteranceAsync, context, cancellationToken: cancellationToken);

//        async ValueTask<IvrWorkflowState?> HandleUtteranceAsync(
//                IvrWorkflowState state,
//                IWorkflowContext context,
//                CancellationToken cancellationToken)
//        {
//            state.AddUtterance(utterance);
//            return state;
//        }
//    }

//    //public async ValueTask HandleTransitionActionAsync(
//    //    TransitionAction action,
//    //    IWorkflowContext context,
//    //    CancellationToken cancellationToken)
//    //{
//    //    await InvokeWithStateAsync(ApplyTransitionAsync, context, cancellationToken: cancellationToken);

//    //    async ValueTask<IvrWorkflowState?> ApplyTransitionAsync(
//    //            IvrWorkflowState state,
//    //            IWorkflowContext context,
//    //            CancellationToken cancellationToken)
//    //    {
//    //        var currentStep = state.CurrentStepName is not null
//    //            ? _workflow.GetStep(state.CurrentStepName)
//    //            : _workflow.Steps[0];

//    //        if (currentStep is null)
//    //        {
//    //            _logger.LogWarning("No current step found for transition action");
//    //            return state;
//    //        }

//    //        if (action.NextStepId is not null &&
//    //            _workflow.GetStep(action.NextStepId) is { } targetStep)
//    //        {
//    //            state.CurrentStepName = targetStep.Id;
//    //            state.CurrentStepIndex = _workflow.Steps.IndexOf(targetStep);
//    //            state.CurrentStepRetryCount = 0;
//    //            _logger.LogInformation(
//    //                "Transitioned to step {StepId} due to action",
//    //                targetStep.Id);
//    //        }
//    //        else
//    //        {
//    //            _logger.LogWarning(
//    //                "Invalid target step ID {TargetStepId} in transition action",
//    //                action.TargetStepId);
//    //        }
//    //        return state;
//    //    }
//    //}



//    //public async ValueTask HandleOrchestratorDecisionAsync(
//    //    RealtimeOrchestratorDecision decision,
//    //    IWorkflowContext context,
//    //    CancellationToken cancellationToken)
//    //{
//    //    await InvokeWithStateAsync(ApplyDecisionAsync, context, cancellationToken: cancellationToken);

//    //    async ValueTask<IvrWorkflowState?> ApplyDecisionAsync(
//    //            IvrWorkflowState state,
//    //            IWorkflowContext context,
//    //            CancellationToken cancellationToken)
//    //    {
//    //        // Update sentiment tracking
//    //        if (Math.Abs(decision.Sentiment) > 0.01)
//    //        {
//    //            state.SentimentScore = decision.Sentiment;
//    //            state.CustomerFrustrationDetected = decision.Sentiment < -0.5;
//    //        }

//    //        // Yield the decision as workflow output so the coordinator can process it
//    //        await context.YieldOutputAsync(decision, cancellationToken);
//    //        return state;
//    //    }
//    //}

//    /// <summary>
//    /// Analyzes agent turns and makes orchestration decisions.
//    /// </summary>
//    public ValueTask HandleAgentTurnFinishedAsync(
//        RealtimeVoiceAgentTurn turn,
//        IWorkflowContext context,
//        CancellationToken cancellationToken)
//    {
//        return InvokeWithStateAsync(AnalyzeTurnAsync, context, cancellationToken: cancellationToken);

//        async ValueTask<IvrWorkflowState?> AnalyzeTurnAsync(
//            IvrWorkflowState? state,
//            IWorkflowContext ctx,
//            CancellationToken ct)
//        {
//            state ??= _initializeState();

//            state.AddTranscriptMessages(turn.TranscriptionMessages);

//            var currentStep = state.CurrentStepName is not null
//                ? _workflow.GetStep(state.CurrentStepName)
//                : _workflow.GetStep(_workflow.InitialStepId);

//            if (currentStep is null)
//            {
//                _logger.LogWarning("No current step found for orchestration");

//                return state;
//            }

//            // Build orchestrator prompt
//            var orchestratorPrompt = BuildOrchestratorSystemPrompt(currentStep, state);

//            // Get orchestrator decision
//            var thread = _orchestratorAgent.GetNewThread();

//            var response = await _orchestratorAgent.RunAsync(
//                messages: [],
//                thread: thread,
//                options: new ChatClientAgentRunOptions {
//                    ChatOptions = new ChatOptions()
//                    {
//                        Instructions = orchestratorPrompt,
//                    },
                   
//                },
//                cancellationToken: ct);

//            var decision = response.Result;
//            _logger.LogDebug(
//                "Orchestrator decision for step {StepId}: Transition={ShouldTransition}, NextStep={NextStep}",
//                currentStep.Id,
//                decision.ShouldTransition,
//                decision.NextStepId);
//            await context.YieldOutputAsync(decision, cancellationToken);

//            return state;
//        }
//    }

//    private string BuildOrchestratorSystemPrompt(RealtimeIvrWorkflowStep currentStep, IvrWorkflowState state)
//    {
//        var stepPrompt = _workflow.BuildPromptForStep(currentStep, state);

//        var sb = new StringBuilder(stepPrompt);

//        sb.AppendLine();
//        sb.AppendLine("# Conversation Transcript");
//        foreach (var msg in state.Transcript)
//        {
//            var role = msg.Role == ChatRole.User ? "User" : "Agent";
//            var text = msg.Text ?? "(audio)";
//            sb.Append(role).Append(": ").AppendLine(text);
//        }

//        return sb.ToString();
//    }
//}
