//using System.ComponentModel;
//using System.Runtime.CompilerServices;
//using System.Security.Claims;
//using System.Text.Json;
//using System.Threading.Channels;
//using Agents.AI.Extensions.AITools;
//using Agents.AI.Extensions.Helpers.Streaming;
//using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
//using Agents.AI.Extensions.RealtimeAgentHelpers;
//using Agents.AI.Extensions.SessionManagement;
//using Agents.AI.Extensions.ToolApproval;
//using Agents.AI.RealtimeVoice;
//using Extensions.AI.Contents;
//using Extensions.AI.RealtimeVoice;
//using Microsoft.Agents.AI;
//using Microsoft.Agents.AI.Workflows;
//using Microsoft.AspNetCore.Components.Web;
//using Microsoft.Extensions.AI;
//using Microsoft.Extensions.DependencyInjection;
//using Microsoft.Extensions.Logging;
//using Microsoft.Extensions.Logging.Abstractions;

//namespace Agents.AI.Extensions.LiveVoice;

//public class ConversationOrchestrationAgentSession : DelegatingRealtimeAIAgent, IUpdateableRealtimeAgent, IAsyncDisposable
//{
//    private readonly IServiceProvider? _scopedServices;
//    private readonly AgentFunctionInvocationMiddleware _delegateFunc;
//    private readonly IAgentSessionRegistry _sessionRegistry;
//    private readonly List<AITool>? _additionalTools;
//    private readonly RealtimeIvrWorkflowDefinition _workflowDefinition;
//    private readonly Func<IvrWorkflowState> _initializeState;
//    private readonly int _maxSessionTokenCount;
//    private readonly ChatClientAgent _orchestratorAgent;
//    private readonly List<RealtimeConversationUtterance> _conversationHistory = [];
//    private readonly Channel<RealtimeVoiceAgentTurn> _turnChannel = Channel.CreateUnbounded<RealtimeVoiceAgentTurn>();
//    private readonly Channel<AgentRunResponseUpdate> _updates = Channel.CreateUnbounded<AgentRunResponseUpdate>();
//    private readonly Lock _stateLock = new();
//    private readonly SemaphoreSlim _configUpdateLock = new(1, 1);

//    internal const string FunctionPrefix = "transition_to_";

//    private static readonly JsonElement handoffSchema = AIFunctionFactory.Create(
//        ([Description("The reason for the handoff")] string? reasonForHandoff) => { }).JsonSchema;
//    private readonly HashSet<string> _handoffFunctionNames = [];


//    // Background orchestrator evaluation
//    private readonly Channel<OrchestratorEvaluationRequest> _orchestratorChannel = Channel.CreateBounded<OrchestratorEvaluationRequest>(
//        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
//    private CancellationTokenSource? _orchestratorCts;
//    private Timer? _stepTimeoutTimer;

//    /// <summary>
//    /// Debounce delay before triggering orchestrator evaluation after last utterance.
//    /// </summary>
//    private readonly TimeSpan _orchestratorDebounceDelay = TimeSpan.FromMilliseconds(500);

//    private readonly IvrWorkflowState _stateCache;
//    private RealtimeIvrStepConfiguration? _currentStepConfig;
//    private LiveConversationAgentSession? _thread;
//    private bool _isInitialized;
//    private bool _disposed;
//    private DateTimeOffset _lastUtteranceTime = DateTimeOffset.MinValue;
//    private int _pendingEvaluationCount;

//    /// <summary>
//    /// Raised when the workflow transitions to a new step.
//    /// </summary>
//    public event Func<RealtimeIvrStepConfiguration, CancellationToken, Task>? OnStepTransition;

//    /// <summary>
//    /// Raised when the workflow completes successfully.
//    /// </summary>
//    public event Func<IvrWorkflowState, string?, CancellationToken, Task>? OnWorkflowCompleted;

//    /// <summary>
//    /// Raised when the workflow fails.
//    /// </summary>
//    public event Func<IvrWorkflowState, string, CancellationToken, Task>? OnWorkflowFailed;

//    /// <summary>
//    /// Raised when escalation to a human is requested.
//    /// </summary>
//    public event Func<string, CancellationToken, Task>? OnEscalationRequested;

//    /// <summary>
//    /// Raised when a step times out (MaxDuration exceeded).
//    /// </summary>
//    public event Func<RealtimeIvrWorkflowStep, IvrWorkflowState, CancellationToken, Task>? OnStepTimeout;

//    // gpt-realtime limit
//    private const int MAX_SESSION_TOKENS = 32000;
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
//    private readonly ILogger<ConversationOrchestrationAgentSession> _logger;

//    /// <summary>
//    /// Gets the current workflow state.
//    /// </summary>
//    public IvrWorkflowState? WorkflowState => _stateCache;

//    /// <summary>
//    /// Gets the current step ID.
//    /// </summary>
//    public string? CurrentStepId => _stateCache?.CurrentStepName;

//    /// <summary>
//    /// Gets whether the workflow has been initialized.
//    /// </summary>
//    public bool IsInitialized => _isInitialized;

//    /// <summary>
//    /// Gets the current step configuration.
//    /// </summary>
//    public RealtimeIvrStepConfiguration? CurrentStepConfiguration => _currentStepConfig;

//    public ConversationOrchestrationAgentSession(
//        AIAgent innerAgent,
//        AIAgent orchestratorAgent,
//        IAgentSessionRegistry sessionRegistry,
//        RealtimeIvrWorkflowDefinition workflowDefinition,
//        AgentFunctionInvocationMiddleware? delegateFunc = null,
//        IEnumerable<IAIToolCollection>? aIToolCollections = null,
//        IServiceProvider? serviceProvider = null,
//        int maxSessionTokenCount = 32000,
//        ILogger<ConversationOrchestrationAgentSession>? logger = null) : base(innerAgent)
//    {
//        _initializeState = () => new IvrWorkflowState()
//        {
//            Status = IvrWorkflowStatus.NotStarted,
//            CurrentStepName = workflowDefinition.GetStep(workflowDefinition.InitialStepId)?.Id,
//        };
//        _orchestratorAgent = (ChatClientAgent)orchestratorAgent;

//        _workflowDefinition = workflowDefinition;
//        _sessionRegistry = sessionRegistry;
//        _scopedServices = serviceProvider;
//        _delegateFunc = delegateFunc ?? DefaultMiddleware;
//        _additionalTools = aIToolCollections?.SelectMany(c => c.AsAITools()).ToList();
//        _maxSessionTokenCount = maxSessionTokenCount;
//        _logger = logger ?? NullLogger<ConversationOrchestrationAgentSession>.Instance;


//        var initialStepId = _workflowDefinition.InitialStepId;

//        _stateCache = _initializeState();
//        _stateCache.Status = IvrWorkflowStatus.Running;
//        _stateCache.CurrentStepName = initialStepId;
//        _stateCache.CurrentStepIndex = 0;
//        _stateCache.StepStartedAt = DateTimeOffset.UtcNow;
//        _stateCache.CurrentStepRetryCount = 0;

//        var step = _workflowDefinition.GetStep(initialStepId)
//           ?? throw new InvalidOperationException($"Initial step '{initialStepId}' not found");

//        var systemPrompt = _workflowDefinition.BuildPromptForStep(initialStepId, _stateCache);
//        _stateCache.CurrentPrompt = systemPrompt;

//        _currentStepConfig = new RealtimeIvrStepConfiguration
//        {
//            StepId = initialStepId,
//            SystemPrompt = systemPrompt,
//            AvailableTools = step.AvailableTools ?? []
//        };
//    }

//    /// <summary>
//    /// Initializes the workflow and prepares the first step.
//    /// Must be called before starting the agent stream.
//    /// </summary>
//    /// <param name="thread">The conversation session thread.</param>
//    /// <param name="cancellationToken">Cancellation token.</param>
//    /// <returns>The initial step configuration.</returns>
//    //public async Task<RealtimeIvrStepConfiguration> InitializeWorkflowAsync(
//    //    LiveConversationAgentSession thread,
//    //    CancellationToken cancellationToken = default)
//    //{
//    //    await _configUpdateLock.WaitAsync(cancellationToken);
//    //    try
//    //    {
//    //        if (_isInitialized)
//    //        {
//    //            throw new InvalidOperationException("Workflow has already been initialized.");
//    //        }

//    //        _thread = thread;

//    //        var initialStepId = _workflowDefinition.InitialStepId;
//    //        var step = _workflowDefinition.GetStep(initialStepId)
//    //                   ?? throw new InvalidOperationException($"Initial step '{initialStepId}' not found");

//    //        _stateCache.Status = IvrWorkflowStatus.Running;
//    //        _stateCache.CurrentStepName = initialStepId;
//    //        _stateCache.CurrentStepIndex = 0;
//    //        _stateCache.StepStartedAt = DateTimeOffset.UtcNow;
//    //        _stateCache.CurrentStepRetryCount = 0;

//    //        // Build the prompt for this step
//    //        var systemPrompt = _workflowDefinition.BuildPromptForStep(initialStepId, _stateCache);
//    //        _stateCache.CurrentPrompt = systemPrompt;

//    //        _currentStepConfig = new RealtimeIvrStepConfiguration
//    //        {
//    //            StepId = initialStepId,
//    //            SystemPrompt = systemPrompt,
//    //            AvailableTools = step.AvailableTools ?? []
//    //        };

//    //        _isInitialized = true;

//    //        // Start background orchestrator processing
//    //        _orchestratorCts = new CancellationTokenSource();
//    //        _orchestratorProcessingTask = ProcessOrchestratorChannelAsync(_orchestratorCts.Token);

//    //        // Start step timeout monitoring if the step has a MaxDuration
//    //        StartStepTimeoutMonitoring(step);

//    //        _logger.LogInformation(
//    //            "Initialized workflow {WorkflowName} at step {StepId}",
//    //            _workflowDefinition.Name,
//    //            initialStepId);

//    //        return _currentStepConfig;
//    //    }
//    //    finally
//    //    {
//    //        _configUpdateLock.Release();
//    //    }
//    //}


//    public Task ConfigureSessionAsync(LiveConversationSessionOptions options, LiveConversationAgentSession thread, CancellationToken cancellationToken = default)
//    {
//        return thread.Session.ConfigureSessionAsync(options, cancellationToken);
//    }

//    public override Task<AgentRunResponse> RunAsync(IEnumerable<ChatMessage> messages, AgentThread? thread = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
//        => InnerAgent.RunAsync(messages, thread, AgentRunOptionsWithFunctionMiddleware(options), cancellationToken);

//    public async Task RunStreamingCoreAsync(IEnumerable<ChatMessage> messages, LiveConversationAgentSession thread, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
//    {
//        var runOptions = AgentRunOptionsWithFunctionMiddleware(options);
//        await foreach (var update in InnerAgent.RunStreamingAsync(messages, thread, runOptions, cancellationToken))
//        {
//            await _updates.Writer.WriteAsync(update, cancellationToken).ConfigureAwait(false);
//        }
//        _updates.Writer.Complete();
//    }

//    public override async IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(IEnumerable<ChatMessage> messages, AgentThread? thread = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
//    {
//        if (thread is not LiveConversationAgentSession conversationSessionThread) throw new ArgumentException("Invalid thread type", nameof(thread));

//        var runOptions = AgentRunOptionsWithFunctionMiddleware(options);
//        List<ConversationSessionUtterance> utterances = [];
//        DateTimeOffset agentTurnStart = DateTimeOffset.UtcNow;
//        DateTimeOffset? agentTurnEnd = null;
//        DateTimeOffset userTurnStart = DateTimeOffset.UtcNow;
//        DateTimeOffset? userTurnEnd = null;

//        await foreach (var update in InnerAgent.RunStreamingAsync(messages, conversationSessionThread, runOptions, cancellationToken))
//        {
//            yield return update;

//            foreach (var content in update.Contents)
//            {
//                if (content is RealtimeVadContent vadContent)
//                {
//                    if (vadContent.VadEvent == VadEventType.InputSpeechStarted)
//                    {
//                        userTurnStart = vadContent.TimeStamp;
//                    }
//                    else if (vadContent.VadEvent == VadEventType.InputSpeechEnded)
//                    {
//                        userTurnEnd = vadContent.TimeStamp;
//                    }
//                    else if (vadContent.VadEvent == VadEventType.OutputSpeechStarted)
//                    {
//                        agentTurnStart = vadContent.TimeStamp;
//                    }
//                    else if (vadContent.VadEvent == VadEventType.OutputSpeechEnded)
//                    {
//                        agentTurnEnd = vadContent.TimeStamp;
//                    }
//                }
//                else if (content is TextContent tc && !string.IsNullOrWhiteSpace(tc.Text))
//                {
//                    if (update.Role == ChatRole.User)
//                    {
//                        await ProcessUtteranceTranscriptAsync(ChatRole.User, userTurnStart, userTurnEnd, tc, cancellationToken).ConfigureAwait(false);
//                    }
//                    else
//                    {
//                        await ProcessUtteranceTranscriptAsync(ChatRole.Assistant, agentTurnStart, agentTurnEnd, tc, cancellationToken).ConfigureAwait(false);
//                    }
//                }
//            }
//        }
//    }

//    private Task ProcessUtteranceTranscriptAsync(ChatRole role, DateTimeOffset turnStartTime, DateTimeOffset? turnEndTime, TextContent transcript, CancellationToken cancellationToken)
//    {
//        if (_stateCache is null || !_isInitialized)
//        {
//            _logger.LogWarning("ProcessUtteranceTranscriptAsync called before workflow initialization");
//            return Task.CompletedTask;
//        }

//        try
//        {
//            _stateCache.AddUtterance(new RealtimeConversationUtterance(new ChatMessage(role, [transcript]))
//            {
//                UtteranceStartTime = turnStartTime,
//                UtteranceEndTime = turnEndTime
//            });
//            _stateCache.TotalTurns++;
//            _lastUtteranceTime = DateTimeOffset.UtcNow;

//            // Queue orchestrator evaluation (non-blocking) after user utterances
//            // The background processor will debounce and evaluate
//            //if (role == ChatRole.User && _thread is not null)
//            //{
//            //    QueueOrchestratorEvaluation();
//            //}
//            if (role == ChatRole.User)
//            {

//                QueueOrchestratorEvaluation();
//            }
//            return Task.CompletedTask;
//        }
//        catch (Exception ex)
//        {
//            _logger.LogError(ex, "Error processing completed turn for workflow");
//            return Task.CompletedTask;
//        }
//    }

//    /// <summary>
//    /// Queues an orchestrator evaluation request. Uses debouncing to avoid excessive calls.
//    /// </summary>
//    private void QueueOrchestratorEvaluation()
//    {
//        var request = new OrchestratorEvaluationRequest
//        {
//            RequestTime = DateTimeOffset.UtcNow,
//            CurrentStepId = _currentStepConfig?.StepId
//        };

//        // Channel is bounded with DropOldest, so this will replace any pending evaluation
//        _orchestratorChannel.Writer.TryWrite(request);
//        Interlocked.Increment(ref _pendingEvaluationCount);
//    }

//    /// <summary>
//    /// Background processor for orchestrator evaluations with debouncing.
//    /// </summary>
//    private async Task ProcessOrchestratorChannelAsync(CancellationToken cancellationToken)
//    {
//        try
//        {
//            await foreach (var request in _orchestratorChannel.Reader.ReadAllAsync(cancellationToken))
//            {
//                try
//                {
//                    // Debounce: wait for a pause in utterances
//                    await Task.Delay(_orchestratorDebounceDelay, cancellationToken).ConfigureAwait(false);

//                    // Check if more utterances came in during the delay
//                    var timeSinceLastUtterance = DateTimeOffset.UtcNow - _lastUtteranceTime;
//                    if (timeSinceLastUtterance < _orchestratorDebounceDelay)
//                    {
//                        // More speech is happening, skip this evaluation
//                        Interlocked.Decrement(ref _pendingEvaluationCount);
//                        continue;
//                    }

//                    // Verify we're still on the same step
//                    if (request.CurrentStepId != _currentStepConfig?.StepId)
//                    {
//                        Interlocked.Decrement(ref _pendingEvaluationCount);
//                        continue;
//                    }

//                    await EvaluateTransitionsAsync(cancellationToken).ConfigureAwait(false);
//                    Interlocked.Decrement(ref _pendingEvaluationCount);
//                }
//                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
//                {
//                    break;
//                }
//                catch (Exception ex)
//                {
//                    _logger.LogError(ex, "Error in background orchestrator evaluation");
//                    Interlocked.Decrement(ref _pendingEvaluationCount);
//                }
//            }
//        }
//        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
//        {
//            // Expected during shutdown
//        }
//    }

//    /// <summary>
//    /// Evaluates whether the current step should transition to a new step using the orchestrator agent.
//    /// </summary>
//    private async Task<RealtimeIvrWorkflowStep?> EvaluateTransitionsAsync(CancellationToken cancellationToken)
//    {
//        if (_stateCache is null || _currentStepConfig is null)
//        {
//            return null;
//        }

//        var currentStep = _workflowDefinition.GetStep(_currentStepConfig.StepId);
//        if (currentStep is null || currentStep.ValidTransitions.Count == 0)
//        {
//            return null;
//        }

//        try
//        {
//            // Build orchestrator prompt with current context
//            var orchestratorPrompt = BuildOrchestratorPrompt(currentStep, _stateCache);
//            var messages = new List<ChatMessage>
//            {
//                new(ChatRole.System, orchestratorPrompt),
//                new(ChatRole.User, "Analyze the conversation and determine if a step transition is needed.")
//            };

//            // Use structured output via tool calling for reliable parsing
//            var analysisResult = await GetOrchestratorDecisionAsync(messages, currentStep, cancellationToken).ConfigureAwait(false);

//            if (analysisResult.ShouldTransition && analysisResult.TargetStepId is not null)
//            {
//                // Validate the target step is in valid transitions
//                if (!currentStep.ValidTransitions.Contains(analysisResult.TargetStepId))
//                {
//                    _logger.LogWarning(
//                        "Orchestrator suggested invalid transition to {TargetStep} from {CurrentStep}",
//                        analysisResult.TargetStepId,
//                        currentStep.Id);
//                    return null;
//                }

//                // Store any extracted data
//                if (analysisResult.ExtractedData is { Count: > 0 })
//                {
//                    foreach (var (key, value) in analysisResult.ExtractedData)
//                    {
//                        _stateCache.Set(key, value);
//                    }
//                }

//                var newStep = await TryTransitionToStepAsync(
//                    analysisResult.TargetStepId,
//                    analysisResult.Reason,
//                    cancellationToken).ConfigureAwait(false);

//                return newStep;
//            }

//            if (analysisResult.ShouldEscalate)
//            {
//                await RequestEscalationAsync(analysisResult.Reason ?? "User requested escalation", cancellationToken).ConfigureAwait(false);
//            }

//            return null;
//        }
//        catch (Exception ex)
//        {
//            _logger.LogError(ex, "Error evaluating step transitions");
//            return null;
//        }
//    }

//    /// <summary>
//    /// Gets the orchestrator's decision using structured output via tool calling.
//    /// </summary>
//    private async Task<OrchestratorAnalysisResult> GetOrchestratorDecisionAsync(
//        List<ChatMessage> messages,
//        RealtimeIvrWorkflowStep currentStep,
//        CancellationToken cancellationToken)
//    {
//        OrchestratorAnalysisResult? capturedResult = null;

//        // Create a function that captures the structured response
//        var decisionFunction = AIFunctionFactory.Create(
//            (bool shouldTransition, string? targetStepId, string reason, bool shouldEscalate, Dictionary<string, object>? extractedData) =>
//            {
//                capturedResult = new OrchestratorAnalysisResult
//                {
//                    ShouldTransition = shouldTransition,
//                    TargetStepId = targetStepId,
//                    Reason = reason,
//                    ShouldEscalate = shouldEscalate,
//                    ExtractedData = extractedData
//                };
//                return "Decision recorded.";
//            },
//            new AIFunctionFactoryOptions
//            {
//                Name = "report_workflow_decision",
//                Description = $"""
//                    Report your analysis of whether the conversation should transition to a new step.
//                    Valid target steps: {string.Join(", ", currentStep.ValidTransitions)}
//                    """
//            });

//        try
//        {
//            // Get the underlying chat client from the orchestrator agent
//            var chatClient = _orchestratorAgent.GetService<IChatClient>();
//            if (chatClient is not null)
//            {
//                var chatOptions = new ChatOptions
//                {
//                    Tools = [decisionFunction],
//                    ToolMode = ChatToolMode.RequireAny
//                };

//                var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);

//                // Process any function calls
//                foreach (var item in response.Messages.SelectMany(m => m.Contents))
//                {
//                    if (item is FunctionCallContent functionCall && functionCall.Name == "report_workflow_decision")
//                    {
//                        var args = functionCall.Arguments is not null
//                            ? new AIFunctionArguments(functionCall.Arguments)
//                            : null;
//                        await decisionFunction.InvokeAsync(args, cancellationToken).ConfigureAwait(false);
//                    }
//                }

//                if (capturedResult is not null)
//                {
//                    return capturedResult;
//                }
//            }
//        }
//        catch (Exception ex)
//        {
//            _logger.LogWarning(ex, "Orchestrator function call failed, falling back to text parsing");
//        }

//        // Fallback: try text-based parsing with agent
//        var fallbackResponse = await _orchestratorAgent.RunAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
//        return ParseOrchestratorResponse(fallbackResponse);
//    }

//    /// <summary>
//    /// Attempts to transition to a new step if guards pass.
//    /// </summary>
//    /// <param name="targetStepId">The step to transition to.</param>
//    /// <param name="reason">Optional reason for the transition.</param>
//    /// <param name="cancellationToken">Cancellation token.</param>
//    /// <returns>The new step if transition succeeded, null if guards failed.</returns>
//    public async Task<RealtimeIvrWorkflowStep?> TryTransitionToStepAsync(
//        string targetStepId,
//        string? reason = null,
//        CancellationToken cancellationToken = default)
//    {
//        await _configUpdateLock.WaitAsync(cancellationToken);
//        try
//        {
//            if (!_isInitialized || _stateCache is null)
//            {
//                throw new InvalidOperationException("Workflow has not been initialized.");
//            }

//            var currentStep = _currentStepConfig?.StepId is not null
//                ? _workflowDefinition.GetStep(_currentStepConfig.StepId)
//                : null;

//            // Validate transition is allowed
//            if (currentStep is not null && !currentStep.ValidTransitions.Contains(targetStepId))
//            {
//                _logger.LogWarning(
//                    "Invalid transition from {CurrentStep} to {TargetStep}",
//                    _currentStepConfig?.StepId,
//                    targetStepId);
//                return null;
//            }

//            var targetStep = _workflowDefinition.GetStep(targetStepId);
//            if (targetStep is null)
//            {
//                _logger.LogWarning("Target step {StepId} not found", targetStepId);
//                return null;
//            }

//            // Evaluate guards
//            var guardResult = await EvaluateGuardsAsync(targetStep, _stateCache, cancellationToken);
//            if (!guardResult.Passed)
//            {
//                _logger.LogWarning(
//                    "Guard failed for step {StepId}: {Reason}",
//                    targetStepId,
//                    guardResult.FailureReason);
//                return null;
//            }

//            // Mark current step as completed
//            if (_currentStepConfig?.StepId is not null)
//            {
//                _stateCache.MarkStepCompleted(_currentStepConfig.StepId);

//                // Execute OnCompleted callback if defined
//                if (currentStep?.OnCompleted is not null)
//                {
//                    await currentStep.OnCompleted(_stateCache, cancellationToken).ConfigureAwait(false);
//                }
//            }

//            // Perform transition
//            _stateCache.CurrentStepName = targetStepId;
//            _stateCache.CurrentStepIndex = _workflowDefinition.GetStepIndex(targetStepId);
//            _stateCache.StepStartedAt = DateTimeOffset.UtcNow;
//            _stateCache.CurrentStepRetryCount = 0;

//            // Build new prompt and configuration
//            var systemPrompt = _workflowDefinition.BuildPromptForStep(targetStepId, _stateCache);
//            _stateCache.CurrentPrompt = systemPrompt;

//            var newConfig = new RealtimeIvrStepConfiguration
//            {
//                StepId = targetStepId,
//                SystemPrompt = systemPrompt,
//                AvailableTools = targetStep.AvailableTools ?? []
//            };

//            _logger.LogInformation(
//                "Transitioned to step {StepId} (reason: {Reason})",
//                targetStepId,
//                reason);

//            // Apply the new configuration to the realtime session
//            await ApplyStepConfigurationAsync(newConfig, _thread!, cancellationToken).ConfigureAwait(false);

//            // Start timeout monitoring for the new step
//            StartStepTimeoutMonitoring(targetStep);

//            return targetStep;
//        }
//        finally
//        {
//            _configUpdateLock.Release();
//        }
//    }

//    /// <summary>
//    /// Forces a transition to a specific step, bypassing guard checks.
//    /// Use with caution - intended for external triggers like tool calls.
//    /// </summary>
//    /// <param name="targetStepId">The step to transition to.</param>
//    /// <param name="reason">Reason for the forced transition.</param>
//    /// <param name="cancellationToken">Cancellation token.</param>
//    /// <returns>The new step if found, null otherwise.</returns>
//    public async Task<RealtimeIvrWorkflowStep?> ForceTransitionToStepAsync(
//        string targetStepId,
//        string? reason = null,
//        CancellationToken cancellationToken = default)
//    {
//        await _configUpdateLock.WaitAsync(cancellationToken);
//        try
//        {
//            if (!_isInitialized || _stateCache is null || _thread is null)
//            {
//                throw new InvalidOperationException("Workflow has not been initialized.");
//            }

//            var targetStep = _workflowDefinition.GetStep(targetStepId);
//            if (targetStep is null)
//            {
//                _logger.LogWarning("Target step {StepId} not found for forced transition", targetStepId);
//                return null;
//            }

//            // Mark current step as completed
//            if (_currentStepConfig?.StepId is not null)
//            {
//                _stateCache.MarkStepCompleted(_currentStepConfig.StepId);
//            }

//            // Perform transition
//            _stateCache.CurrentStepName = targetStepId;
//            _stateCache.CurrentStepIndex = _workflowDefinition.GetStepIndex(targetStepId);
//            _stateCache.StepStartedAt = DateTimeOffset.UtcNow;
//            _stateCache.CurrentStepRetryCount = 0;

//            // Build new prompt and configuration
//            var systemPrompt = _workflowDefinition.BuildPromptForStep(targetStepId, _stateCache);
//            _stateCache.CurrentPrompt = systemPrompt;

//            var newConfig = new RealtimeIvrStepConfiguration
//            {
//                StepId = targetStepId,
//                SystemPrompt = systemPrompt,
//                AvailableTools = targetStep.AvailableTools ?? []
//            };

//            _logger.LogInformation(
//                "Forced transition to step {StepId} (reason: {Reason})",
//                targetStepId,
//                reason ?? "External trigger");

//            await ApplyStepConfigurationAsync(newConfig, _thread, cancellationToken).ConfigureAwait(false);

//            // Start timeout monitoring for the new step
//            StartStepTimeoutMonitoring(targetStep);

//            return targetStep;
//        }
//        finally
//        {
//            _configUpdateLock.Release();
//        }
//    }

//    /// <summary>
//    /// Starts or restarts the step timeout timer based on the step's MaxDuration.
//    /// </summary>
//    private void StartStepTimeoutMonitoring(RealtimeIvrWorkflowStep step)
//    {
//        _stepTimeoutTimer?.Dispose();
//        _stepTimeoutTimer = null;

//        if (step.MaxDuration.HasValue && step.MaxDuration.Value > TimeSpan.Zero)
//        {
//            _stepTimeoutTimer = new Timer(
//                OnStepTimeoutElapsed,
//                step,
//                step.MaxDuration.Value,
//                Timeout.InfiniteTimeSpan);

//            _logger.LogDebug(
//                "Started timeout timer for step {StepId} with duration {Duration}",
//                step.Id,
//                step.MaxDuration.Value);
//        }
//    }

//    private async void OnStepTimeoutElapsed(object? state)
//    {
//        if (state is not RealtimeIvrWorkflowStep step || _stateCache is null)
//        {
//            return;
//        }

//        // Verify we're still on the same step
//        if (_currentStepConfig?.StepId != step.Id)
//        {
//            return;
//        }

//        _logger.LogWarning(
//            "Step {StepId} timed out after {Duration}",
//            step.Id,
//            step.MaxDuration);

//        try
//        {
//            if (OnStepTimeout is not null)
//            {
//                await OnStepTimeout(step, _stateCache, CancellationToken.None);
//            }

//            // Check if step has a timeout transition defined
//            var timeoutTransition = step.ConversationState.Transitions?
//                .FirstOrDefault(t => t.Condition?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true);

//            if (timeoutTransition is not null)
//            {
//                await TryTransitionToStepAsync(timeoutTransition.NextStep, "Step timeout", CancellationToken.None)
//                    .ConfigureAwait(false);
//            }
//        }
//        catch (Exception ex)
//        {
//            _logger.LogError(ex, "Error handling step timeout for {StepId}", step.Id);
//        }
//    }

//    /// <summary>
//    /// Records extracted data into the workflow state.
//    /// </summary>
//    public async Task RecordExtractedDataAsync(
//        Dictionary<string, object> data,
//        CancellationToken cancellationToken = default)
//    {
//        await _configUpdateLock.WaitAsync(cancellationToken);
//        try
//        {
//            if (_stateCache is null)
//            {
//                return;
//            }

//            foreach (var (key, value) in data)
//            {
//                _stateCache.Set(key, value);
//                _logger.LogDebug("Stored extracted data: {Key} = {Value}", key, value);
//            }
//        }
//        finally
//        {
//            _configUpdateLock.Release();
//        }
//    }

//    /// <summary>
//    /// Completes the workflow successfully.
//    /// </summary>
//    public async Task CompleteWorkflowAsync(
//        string? completionMessage = null,
//        CancellationToken cancellationToken = default)
//    {
//        await _configUpdateLock.WaitAsync(cancellationToken);
//        try
//        {
//            if (_stateCache is null)
//            {
//                return;
//            }

//            _stateCache.Status = IvrWorkflowStatus.Completed;

//            _logger.LogInformation("Workflow {WorkflowName} completed successfully", _workflowDefinition.Name);

//            if (OnWorkflowCompleted is not null)
//            {
//                await OnWorkflowCompleted(_stateCache, completionMessage, cancellationToken);
//            }
//        }
//        finally
//        {
//            _configUpdateLock.Release();
//        }
//    }

//    /// <summary>
//    /// Fails the workflow with an error.
//    /// </summary>
//    public async Task FailWorkflowAsync(
//        string errorMessage,
//        CancellationToken cancellationToken = default)
//    {
//        await _configUpdateLock.WaitAsync(cancellationToken);
//        try
//        {
//            if (_stateCache is null)
//            {
//                return;
//            }

//            _stateCache.Status = IvrWorkflowStatus.Failed;
//            _stateCache.SetErrorMessage(errorMessage);

//            _logger.LogError("Workflow {WorkflowName} failed: {Error}", _workflowDefinition.Name, errorMessage);

//            if (OnWorkflowFailed is not null)
//            {
//                await OnWorkflowFailed(_stateCache, errorMessage, cancellationToken);
//            }
//        }
//        finally
//        {
//            _configUpdateLock.Release();
//        }
//    }

//    /// <summary>
//    /// Requests escalation to a human agent.
//    /// </summary>
//    public async Task RequestEscalationAsync(
//        string reason,
//        CancellationToken cancellationToken = default)
//    {
//        await _configUpdateLock.WaitAsync(cancellationToken);
//        try
//        {
//            if (_stateCache is null)
//            {
//                return;
//            }

//            _stateCache.Status = IvrWorkflowStatus.TransferRequested;

//            _logger.LogInformation("Escalation requested: {Reason}", reason);

//            if (OnEscalationRequested is not null)
//            {
//                await OnEscalationRequested(reason, cancellationToken);
//            }
//        }
//        finally
//        {
//            _configUpdateLock.Release();
//        }
//    }

//    /// <summary>
//    /// Checks if the current step's requirements are satisfied.
//    /// </summary>
//    public async Task<bool> ValidateCurrentStepAsync(CancellationToken cancellationToken = default)
//    {
//        await _configUpdateLock.WaitAsync(cancellationToken);
//        try
//        {
//            if (_currentStepConfig?.StepId is null || _stateCache is null)
//            {
//                return false;
//            }

//            var step = _workflowDefinition.GetStep(_currentStepConfig.StepId);
//            if (step is null)
//            {
//                return false;
//            }

//            return await ValidateStepAsync(step, _stateCache, cancellationToken);
//        }
//        finally
//        {
//            _configUpdateLock.Release();
//        }
//    }


//    private async Task ApplyStepConfigurationAsync(
//        RealtimeIvrStepConfiguration config,
//        LiveConversationAgentSession thread,
//        CancellationToken cancellationToken)
//    {
//        _currentStepConfig = config;

//        _logger.LogInformation(
//            "Applying step configuration for step {StepId} with {ToolCount} tools",
//            config.StepId,
//            config.AvailableTools.Count);

//        // Update the realtime session with new instructions and tools
//        var sessionOptions = new LiveConversationSessionOptions
//        {
//            Instructions = config.SystemPrompt,
//            Tools = [.. config.AvailableTools]
//        };

//        await ConfigureSessionAsync(sessionOptions, thread, cancellationToken).ConfigureAwait(false);

//        if (OnStepTransition is not null)
//        {
//            await OnStepTransition(config, cancellationToken);
//        }
//    }

//    private string BuildOrchestratorPrompt(RealtimeIvrWorkflowStep currentStep, IvrWorkflowState state)
//    {
//        var recentTranscript = GetRecentTranscriptContext(state);
//        var validTransitions = string.Join(", ", currentStep.ValidTransitions);
//        var exitConditions = string.Join("; ", currentStep.ConversationState.Transitions?.Select(t => $"{t.Condition} -> {t.NextStep}") ?? []);
//        var requiredKeys = string.Join(", ", currentStep.RequiredStateKeys);
//        var collectedData = string.Join("\n", state.Keys.Select(k => $"- {k}: {state.Get<object>(k)}"));

//        return $"""
//            {OrchestratorRolePreamble}

//            # Current Step
//            Step ID: {currentStep.Id}
//            Description: {currentStep.ConversationState.Description}
//            Goal: {currentStep.ConversationState.Goal ?? "Not specified"}
//            Exit Conditions: {exitConditions}

//            # Valid Transitions
//            {validTransitions}

//            # Required State Keys for Current Step
//            {requiredKeys}

//            # Collected Data
//            {collectedData}

//            # Recent Conversation
//            {recentTranscript}

//            # Response Format
//            Respond with a JSON object:
//            {"{"}
//                "shouldTransition": true or false,
//                "targetStepId": "step_id" or null,
//                "reason": "brief explanation",
//                "shouldEscalate": true or false,
//                "extractedData": {"{"} "key": "value" {"}"}
//            {"}"}
//            """;
//    }

//    private string GetRecentTranscriptContext(IvrWorkflowState state)
//    {
//        // Use conversation history which has timing information
//        var utterances = state.ConversationHistory.TakeLast(MaxTranscriptMessages).ToList();

//        if (utterances.Count == 0)
//        {
//            return "(No conversation yet)";
//        }

//        var tokenCount = 0;
//        var result = new List<string>();

//        // Process in reverse to respect token limit from most recent
//        for (int i = utterances.Count - 1; i >= 0; i--)
//        {
//            var utterance = utterances[i];
//            var text = utterance.Message.Text ?? string.Empty;
//            var estimatedTokens = text.Length / 4; // Rough estimate

//            if (tokenCount + estimatedTokens > MaxContextTokenEstimate)
//            {
//                break;
//            }

//            tokenCount += estimatedTokens;

//            // Format with role label and optional timing
//            var roleLabel = utterance.Message.Role == ChatRole.User ? "Customer" : "Agent";
//            var timing = utterance.TurnDuration.HasValue
//                ? $" [{utterance.TurnDuration.Value.TotalSeconds:F1}s]"
//                : "";

//            result.Insert(0, $"{roleLabel}{timing}: {text}");
//        }

//        return string.Join("\n\n", result);
//    }

//    private static OrchestratorAnalysisResult ParseOrchestratorResponse(AgentRunResponse response)
//    {
//        try
//        {
//            var responseText = response.Messages.LastOrDefault()?.Text ?? string.Empty;

//            // Extract JSON from response (handle markdown code blocks)
//            var jsonStart = responseText.IndexOf('{');
//            var jsonEnd = responseText.LastIndexOf('}');

//            if (jsonStart >= 0 && jsonEnd > jsonStart)
//            {
//                var json = responseText[jsonStart..(jsonEnd + 1)];
//                var parsed = System.Text.Json.JsonSerializer.Deserialize<OrchestratorJsonResponse>(json);

//                if (parsed is not null)
//                {
//                    return new OrchestratorAnalysisResult
//                    {
//                        ShouldTransition = parsed.ShouldTransition,
//                        TargetStepId = parsed.TargetStepId,
//                        Reason = parsed.Reason,
//                        ShouldEscalate = parsed.ShouldEscalate,
//                        ExtractedData = parsed.ExtractedData
//                    };
//                }
//            }
//        }
//        catch
//        {
//            // Fall through to default result
//        }

//        return new OrchestratorAnalysisResult();
//    }

//    private static async Task<IvrGuardResult> EvaluateGuardsAsync(
//        RealtimeIvrWorkflowStep step,
//        IvrWorkflowState state,
//        CancellationToken cancellationToken)
//    {
//        // Check authentication level
//        if (step.RequiredAuthLevel > state.AuthLevel)
//        {
//            return IvrGuardResult.Fail(
//                $"Insufficient authentication level. Required: {step.RequiredAuthLevel}, Current: {state.AuthLevel}");
//        }

//        // Evaluate step guards
//        foreach (var guard in step.Guards)
//        {
//            var result = await guard.EvaluateAsync(state, cancellationToken);
//            if (!result.Passed)
//            {
//                return result;
//            }
//        }

//        return IvrGuardResult.Pass();
//    }

//    private static async Task<bool> ValidateStepAsync(
//        RealtimeIvrWorkflowStep step,
//        IvrWorkflowState state,
//        CancellationToken cancellationToken)
//    {
//        // Check required state keys
//        foreach (var key in step.RequiredStateKeys)
//        {
//            if (!state.Has(key))
//            {
//                return false;
//            }
//        }

//        // Evaluate validators
//        foreach (var validator in step.Validators)
//        {
//            var result = await validator.ValidateAsync(state, cancellationToken);
//            if (!result.Passed)
//            {
//                return false;
//            }
//        }

//        return true;
//    }

//    private static ValueTask<object?> DefaultMiddleware(AIAgent agent, AIFunctionArguments arguments, AIFunction function, Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> next, CancellationToken ct)
//    {
//        return next(arguments, ct); // Pass through
//    }


//    private AgentRunOptions? AgentRunOptionsWithFunctionMiddleware(AgentRunOptions? options)
//    {
//        if (options is null || options.GetType() == typeof(AgentRunOptions))
//        {
//            options = new RealtimeAgentRunOptions();
//        }

//        if (options is not RealtimeAgentRunOptions aco)
//        {
//            throw new NotSupportedException($"Function Invocation Middleware is only supported without options or with {nameof(RealtimeAgentRunOptions)}.");
//        }

//        var originalClientFactory = aco.ConversationClientFactory;

//        aco.ConversationClientFactory = client =>
//        {
//            var builder = client.AsBuilder();

//            if (originalClientFactory is not null)
//            {
//                builder.Use(originalClientFactory);
//            }

//            IEnumerable<AITool> ProcessTools(IEnumerable<AITool> tools)
//            {
//                foreach (var tool in tools)
//                {
//                    if (tool is AIFunction funcTool)
//                    {
//                        var authorizedFunc = new AuthorizingAgentFunction(this, funcTool, _delegateFunc);
//                        yield return authorizedFunc;
//                    }
//                    else
//                    {
//                        yield return tool;
//                    }
//                }
//            }
//            ;

//            return builder.ConfigureOptions(
//                    session => session.Tools = session.Tools is null ? null : [.. ProcessTools(session.Tools)],
//                    response =>
//                    {
//                        response ??= new();
//                        response.Tools ??= [];
//                        if (_additionalTools is not null)
//                        {
//                            response.Tools = [.. response.Tools, .. _additionalTools];
//                        }
//                        response.Tools = [.. ProcessTools(response.Tools)];
//                    }
//                )
//                .Build(_scopedServices);
//        };


//        return options;
//    }

//    public override object? GetService(Type serviceType, object? serviceKey = null) =>
//        serviceType == typeof(AIAgent) ? this :
//        serviceKey == null ? _scopedServices?.GetService(serviceType) : _scopedServices?.GetKeyedService(serviceType, serviceKey) ??
//        base.GetService(serviceType, serviceKey);


//    internal sealed class AuthorizingAgentFunction : DelegatingAIFunction
//    {
//        private readonly ILogger<AuthorizingAgentFunction>? _logger;
//        private readonly AIAgent _agent;
//        private readonly AgentFunctionInvocationMiddleware? _next;
//        // used to mark that this function follows the approval workflow
//        //private readonly ApprovalRequiredAIFunction? _marker;

//        public readonly List<IToolApprovalRequirement>? ToolRequirements;

//        public AuthorizingAgentFunction(AIAgent agent, AIFunction innerFunction, AgentFunctionInvocationMiddleware next) : base(innerFunction)
//        {
//            _logger = GetService<ILoggerFactory>()?.CreateLogger<AuthorizingAgentFunction>();
//            _agent = agent;
//            ToolRequirements = innerFunction.UnderlyingMethod?.GetCustomAttributes(true)
//                .Where(attr => attr is IToolApprovalRequirementData)
//                .SelectMany(attr => ((IToolApprovalRequirementData)attr).GetRequirements())
//                .ToList();
//            _next = next;
//            //_marker = ToolRequirements is null or { Count: 0 } ? null : new ApprovalRequiredAIFunction(this);
//        }

//        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
//        {
//            if (ToolRequirements is null or { Count: 0 } || GetService<IToolApprovalHandlerProvider>() is not IToolApprovalHandlerProvider toolApprovalHandlerProvider)
//            {
//                return await base.InvokeCoreAsync(arguments, cancellationToken);
//            }

//            var invokingIdentity = arguments.Services?.GetService<ClaimsPrincipal>() ?? GetService<ClaimsPrincipal>();
//            var approvalContext = new ToolApprovalContext(this, arguments, _agent, ToolRequirements, invokingIdentity);
//            var handlers = await toolApprovalHandlerProvider.GetHandlersAsync(approvalContext).ConfigureAwait(false);

//            foreach (var handler in handlers)
//            {
//                await handler.HandleAsync(approvalContext).ConfigureAwait(false);
//            }

//            if (!approvalContext.HasSucceeded)
//            {
//                var failure = new ToolApprovalFailure(InnerFunction, arguments, [.. approvalContext.PendingRequirements], [.. approvalContext.FailureResponses], approvalContext.PendingRequirements is { Count: 0 });
//                _logger?.LogWarning("Function '{FunctionName}' invocation denied due to failed tool approval requirements.", InnerFunction.Name);
//                return failure.FailureResponseMessage.Text;
//            }

//            if (_next is not null)
//            {
//                return await _next.Invoke(_agent, arguments, InnerFunction, base.InvokeCoreAsync, cancellationToken);
//            }
//            else
//            {
//                return await base.InvokeCoreAsync(arguments, cancellationToken);
//            }
//        }

//        public override object? GetService(Type serviceType, object? serviceKey = null) =>
//            //serviceType == typeof(ApprovalRequiredAIFunction) ? _marker :
//            serviceType == typeof(IEnumerable<IToolApprovalRequirement>) ? ToolRequirements :
//            serviceType.IsInstanceOfType(typeof(AIAgent)) ? _agent :
//            _agent.GetService(serviceType, serviceKey) ??
//            base.GetService(serviceType, serviceKey);
//    }

//    /// <summary>
//    /// Disposes the orchestration session and cleans up background tasks.
//    /// </summary>
//    public async ValueTask DisposeAsync()
//    {
//        if (_disposed)
//        {
//            return;
//        }

//        _disposed = true;

//        // Stop timeout timer
//        _stepTimeoutTimer?.Dispose();
//        _stepTimeoutTimer = null;

//        // Cancel and await background orchestrator processing
//        if (_orchestratorCts is not null)
//        {
//            await _orchestratorCts.CancelAsync();
//            _orchestratorChannel.Writer.Complete();

//            if (_orchestratorProcessingTask is not null)
//            {
//                try
//                {
//                    await _orchestratorProcessingTask.ConfigureAwait(false);
//                }
//                catch (OperationCanceledException)
//                {
//                    // Expected
//                }
//            }

//            _orchestratorCts.Dispose();
//        }

//        _configUpdateLock.Dispose();
//    }

//}

///// <summary>
///// Result from the orchestrator agent's analysis of the conversation.
///// </summary>
//internal sealed class OrchestratorAnalysisResult
//{
//    public bool ShouldTransition { get; init; }
//    public string? TargetStepId { get; init; }
//    public string? Reason { get; init; }
//    public bool ShouldEscalate { get; init; }
//    public Dictionary<string, object>? ExtractedData { get; init; }
//}

///// <summary>
///// Request for background orchestrator evaluation.
///// </summary>
//internal sealed class OrchestratorEvaluationRequest
//{
//    public DateTimeOffset RequestTime { get; init; }
//    public string? CurrentStepId { get; init; }
//}

///// <summary>
///// JSON response structure from the orchestrator agent.
///// </summary>
//internal sealed class OrchestratorJsonResponse
//{
//    [System.Text.Json.Serialization.JsonPropertyName("shouldTransition")]
//    public bool ShouldTransition { get; set; }

//    [System.Text.Json.Serialization.JsonPropertyName("targetStepId")]
//    public string? TargetStepId { get; set; }

//    [System.Text.Json.Serialization.JsonPropertyName("reason")]
//    public string? Reason { get; set; }

//    [System.Text.Json.Serialization.JsonPropertyName("shouldEscalate")]
//    public bool ShouldEscalate { get; set; }

//    [System.Text.Json.Serialization.JsonPropertyName("extractedData")]
//    public Dictionary<string, object>? ExtractedData { get; set; }
//}

//public sealed class ConversationSessionUtterance(ChatMessage message)
//{
//    public DateTimeOffset UtteranceStartTime { get; set; } = DateTimeOffset.UtcNow;

//    public DateTimeOffset? UtteranceEndTime { get; set; }

//    public ChatMessage Message { get; set; } = message;

//    public TimeSpan? TurnDuration => UtteranceEndTime.HasValue ? UtteranceEndTime - UtteranceStartTime : null;
//}
