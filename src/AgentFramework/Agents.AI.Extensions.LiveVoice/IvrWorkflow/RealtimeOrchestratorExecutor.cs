using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Agents.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.Extensions.LiveVoice.IvrWorkflow;

/// <summary>
/// Orchestrator decision output for Realtime IVR workflows.
/// </summary>
[Description("Decision output from the orchestrator analyzing a voice conversation turn. Determines workflow progression, data extraction, and escalation needs.")]
public sealed record RealtimeOrchestratorDecision
{
    /// <summary>
    /// Whether a step transition should occur.
    /// </summary>
    [Description("Set to true if the conversation indicates the current step's exit condition has been met and a transition to another step should occur.")]
    public bool ShouldTransition { get; init; }

    /// <summary>
    /// Target step ID to transition to.
    /// </summary>
    [Description("The ID of the next workflow step to transition to. Must match one of the valid transition targets defined for the current step. Required when ShouldTransition is true.")]
    public string? NextStepId { get; init; }

    /// <summary>
    /// Reason for the transition.
    /// </summary>
    [Description("A brief explanation of why this transition is being recommended, based on the conversation analysis.")]
    public string? TransitionReason { get; init; }

    /// <summary>
    /// Data extracted from the conversation.
    /// </summary>
    [Description("Key-value pairs of data extracted from the user's responses during this turn. Keys should match the required state keys defined for the current step.")]
    public Dictionary<string, object>? ExtractedData { get; init; }

    /// <summary>
    /// Whether the workflow should end.
    /// </summary>
    [Description("Set to true if the conversation indicates the entire workflow should complete, such as when the user's request has been fully resolved.")]
    public bool ShouldEndWorkflow { get; init; }

    /// <summary>
    /// Whether to escalate to a human.
    /// </summary>
    [Description("Set to true if the user explicitly requests human assistance, expresses significant frustration, or the situation requires human intervention.")]
    public bool ShouldEscalate { get; init; }

    /// <summary>
    /// Reason for escalation.
    /// </summary>
    [Description("A brief explanation of why escalation to a human agent is being recommended. Required when ShouldEscalate is true.")]
    public string? EscalationReason { get; init; }

    /// <summary>
    /// Detected sentiment score (-1.0 to 1.0).
    /// </summary>
    [Description("The detected emotional sentiment of the user in this turn. Range: -1.0 (very negative/frustrated) to 1.0 (very positive/satisfied). Use 0.0 for neutral.")]
    public double Sentiment { get; init; }

    /// <summary>
    /// Confidence score for this decision.
    /// </summary>
    [Description("How confident the orchestrator is in this decision. Range: 0.0 (no confidence) to 1.0 (fully confident). Default is 1.0.")]
    public double Confidence { get; init; } = 1.0;
}
public sealed class RealtimeAgentExecutor : Executor
{
    private readonly RealtimeAIAgent _agent;
    private ConversationSessionThread? _thread;
    public RealtimeAgentExecutor(string id, RealtimeAIAgent agent)
        : base(id)
    {
        _agent = agent;
    }
    
    public async Task<ConversationSessionThread> EnsureThreadAsync(CancellationToken cancellationToken)
    {
        return _thread ??= await _agent.GetNewThreadAsync(cancellationToken);
    }

    protected override RouteBuilder ConfigureRoutes(RouteBuilder routeBuilder)
    {
        return routeBuilder;
    }

    public async Task HandleDataAsync(DataContent dataContent, IWorkflowContext context, CancellationToken cancellationToken)
    {
        var thread = await EnsureThreadAsync(cancellationToken);
        await _agent.SendAudioToRunAsync(dataContent, thread, cancellationToken).ConfigureAwait(false);
    }

    public async Task HandleMessageAsync(ChatMessage message, IWorkflowContext context, CancellationToken cancellationToken)
    {
        var thread = await EnsureThreadAsync(cancellationToken);
        await _agent.SendMessagesToRunAsync([message], thread, cancellationToken).ConfigureAwait(false);
    }
}
/// <summary>
/// Executor that orchestrates step transitions by analyzing conversation transcripts
/// and making decisions about workflow progression.
/// </summary>
public sealed class RealtimeIvrOrchestratorExecutor : StatefulExecutor<IvrWorkflowState>
{
    private readonly ChatClientAgent _orchestratorAgent;
    private readonly RealtimeIvrWorkflowDefinition _workflow;
    private readonly ILogger<RealtimeIvrOrchestratorExecutor> _logger;
    private readonly ChatOptions _chatOptions;
    private readonly JsonSerializerOptions _jsonOptions = LiveVoiceJsonUtilities.DefaultOptions;
    private static readonly Func<IvrWorkflowState> initializeState = () => new IvrWorkflowState();

    /// <summary>
    /// Maximum number of transcript messages to include in context.
    /// </summary>
    private const int MaxTranscriptMessages = 15;

    /// <summary>
    /// Approximate token limit for conversation context to avoid exceeding model limits.
    /// </summary>
    private const int MaxContextTokenEstimate = 2000;

    /// <summary>
    /// Static orchestrator role preamble (cached to avoid reconstruction).
    /// </summary>
    private const string OrchestratorRolePreamble = """
        # Role
        You are a workflow orchestrator analyzing voice conversations to determine step transitions.
        Your job is to observe the conversation and decide when the current step's goals have been met.

        # Decision Guidelines
        - Only recommend transitions when exit conditions are clearly satisfied
        - Extract data accurately from user responses
        - Flag escalation only for explicit requests or significant frustration
        - Be conservative with transitions - prefer staying in current step if uncertain
        """;

    public RealtimeIvrOrchestratorExecutor(
        string id,
        AIAgent orchestratorAgent,
        RealtimeIvrWorkflowDefinition workflow,
        ILogger<RealtimeIvrOrchestratorExecutor>? logger = null)
        : base(id, () => new IvrWorkflowState(), declareCrossRunShareable: true)
    {
        if(orchestratorAgent is not ChatClientAgent chatClientAgent) 
        {
            throw new ArgumentException("Orchestrator agent must be a ChatClientAgent", nameof(orchestratorAgent));
        }
        _orchestratorAgent = chatClientAgent;
        _workflow = workflow;
        _logger = logger ?? NullLogger<RealtimeIvrOrchestratorExecutor>.Instance;

        var jsonSchema = AIJsonUtilities.CreateJsonSchema(typeof(RealtimeOrchestratorDecision));
        _chatOptions = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(jsonSchema)
        };
    }

    /// <inheritdoc/>
    protected override RouteBuilder ConfigureRoutes(RouteBuilder routeBuilder)
    {
        return routeBuilder
            .AddHandler<RealtimeVoiceAgentTurn>(HandleAgentTurnAsync);
    }

    /// <summary>
    /// Analyzes agent turns and makes orchestration decisions.
    /// </summary>
    private ValueTask HandleAgentTurnAsync(
        RealtimeVoiceAgentTurn turn,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        return InvokeWithStateAsync(AnalyzeTurnAsync, context, cancellationToken: cancellationToken);

        async ValueTask<IvrWorkflowState?> AnalyzeTurnAsync(
            IvrWorkflowState? state,
            IWorkflowContext ctx,
            CancellationToken ct)
        {
            state ??= initializeState();

            state.AddTranscriptMessages(turn.TranscriptionMessages);

            var currentStep = state.CurrentStepName is not null
                ? _workflow.GetStep(state.CurrentStepName)
                : _workflow.Steps[0];

            if (currentStep is null)
            {
                _logger.LogWarning("No current step found for orchestration");

                return state;
            }

            // Build orchestrator prompt
            var orchestratorMessages = BuildOrchestratorMessages(state, currentStep, turn);

            // Get orchestrator decision
            var thread = _orchestratorAgent.GetNewThread();

            var response = await _orchestratorAgent.RunAsync<RealtimeOrchestratorDecision>(
                messages: orchestratorMessages,
                thread: thread,
                serializerOptions: _jsonOptions,
                options: new ChatClientAgentRunOptions { ChatOptions = _chatOptions },
                useJsonSchemaResponseFormat: true,
                cancellationToken: ct);

            var decision = response.Result;
            _logger.LogDebug(
                "Orchestrator decision for step {StepId}: Transition={ShouldTransition}, NextStep={NextStep}",
                currentStep.Id,
                decision.ShouldTransition,
                decision.NextStepId);

            // Apply decision
            await ApplyDecisionAsync(ctx, state, decision, ct);

            return state;
        }
    }

    private List<ChatMessage> BuildOrchestratorMessages(
        IvrWorkflowState state,
        RealtimeIvrWorkflowStep currentStep,
        RealtimeVoiceAgentTurn turn)
    {
        var systemPrompt = BuildOrchestratorSystemPrompt(currentStep);
        var conversationContext = BuildConversationContext(state, turn);

        return
        [
            new ChatMessage(ChatRole.System, systemPrompt),
            new ChatMessage(ChatRole.User, conversationContext)
        ];
    }

    private string BuildOrchestratorSystemPrompt(RealtimeIvrWorkflowStep currentStep)
    {
        // Use interpolated string for better performance with the static preamble
        return $"""
            {OrchestratorRolePreamble}

            # Current Step
            - ID: {currentStep.Id}
            - Description: {currentStep.ConversationState.Description}
            {(currentStep.ConversationState.Goal is not null ? $"- Goal: {currentStep.ConversationState.Goal}" : "")}
            {(currentStep.ConversationState.ExitWhen is not null ? $"- Exit Condition: {currentStep.ConversationState.ExitWhen}" : "")}

            # Required Data to Collect
            {FormatRequiredDataKeys(currentStep)}

            # Valid Transitions
            {FormatTransitions(currentStep)}
            """;
    }

    private static string FormatRequiredDataKeys(RealtimeIvrWorkflowStep step)
    {
        if (step.RequiredStateKeys.Count == 0)
        {
            return "- None specified";
        }

        return string.Join(Environment.NewLine, step.RequiredStateKeys.Select(k => $"- {k}"));
    }

    private static string FormatTransitions(RealtimeIvrWorkflowStep step)
    {
        if (step.ConversationState.Transitions is not { Count: > 0 })
        {
            return "- No transitions defined (final step)";
        }

        return string.Join(
            Environment.NewLine,
            step.ConversationState.Transitions.Select(t => $"- {t.NextStep}: {t.Condition}"));
    }

    private string BuildConversationContext(IvrWorkflowState state, RealtimeVoiceAgentTurn turn)
    {
        var sb = new StringBuilder(MaxContextTokenEstimate);

        // Get recent messages with token-aware truncation
        var recentMessages = GetRecentMessagesWithinTokenLimit(state.Transcript, MaxTranscriptMessages);

        sb.AppendLine("# Recent Conversation");
        foreach (var msg in recentMessages)
        {
            var role = msg.Role == ChatRole.User ? "User" : "Agent";
            var text = msg.Text ?? "(audio)";
            sb.Append(role).Append(": ").AppendLine(text);
        }

        sb.AppendLine();
        sb.AppendLine("# Latest Turn");
        foreach (var msg in turn.TranscriptionMessages)
        {
            var role = msg.Role == ChatRole.User ? "User" : "Agent";
            var text = msg.Text ?? "(audio)";
            sb.Append(role).Append(": ").AppendLine(text);
        }

        sb.AppendLine();
        sb.AppendLine("# Already Collected Data");
        var allKeys = state.Keys;
        if (allKeys.Count > 0)
        {
            foreach (var key in allKeys)
            {
                if (state.TryGet<object>(key, out var value))
                {
                    sb.Append("- ").Append(key).Append(": ").AppendLine(value?.ToString() ?? "(null)");
                }
            }
        }
        else
        {
            sb.AppendLine("- None");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Gets recent messages while staying within an estimated token budget.
    /// </summary>
    private static IEnumerable<ChatMessage> GetRecentMessagesWithinTokenLimit(
        IReadOnlyList<ChatMessage> transcript,
        int maxMessages)
    {
        if (transcript.Count <= maxMessages)
        {
            return transcript;
        }

        // Take last N messages, prioritizing recent context
        var startIndex = Math.Max(0, transcript.Count - maxMessages);
        var result = new List<ChatMessage>(maxMessages);

        for (var i = startIndex; i < transcript.Count; i++)
        {
            result.Add(transcript[i]);
        }

        return result;
    }

    private async Task ApplyDecisionAsync(
        IWorkflowContext context,
        IvrWorkflowState state,
        RealtimeOrchestratorDecision decision,
        CancellationToken cancellationToken)
    {
        // Update sentiment tracking
        if (Math.Abs(decision.Sentiment) > 0.01)
        {
            state.SentimentScore = decision.Sentiment;
            state.CustomerFrustrationDetected = decision.Sentiment < -0.5;
        }

        // Yield the decision as workflow output so the coordinator can process it
        await context.YieldOutputAsync(decision, cancellationToken);
    }
}

#region Orchestrator Events

// Note: These event types are kept for backward compatibility but the new approach
// uses the RealtimeOrchestratorDecision output directly with the workflow coordinator.

/// <summary>
/// Event raised when escalation to a human is requested.
/// </summary>
public sealed record EscalationRequestedEvent(string? Reason);

/// <summary>
/// Event raised when the workflow completes.
/// </summary>
public sealed record WorkflowCompletedEvent(IvrWorkflowState FinalState);

#endregion
