using System.Runtime.CompilerServices;
using System.Security.Claims;
using Agents.AI.Extensions.AITools;
using Agents.AI.Extensions.LiveVoice;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.Extensions.SessionManagement;
using Agents.AI.Extensions.ToolApproval;
using Agents.AI.RealtimeVoice;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.VoiceAgent;

/// <summary>
/// Default scoped <see cref="AIAgent"/> implementation for IVR call sessions.
/// <para>
/// Wraps an inner realtime agent, applies function-invocation middleware for
/// tool authorisation, and owns the IVR workflow state machine (step configuration,
/// utterance tracking, step transitions). The transport layer simply proxies audio
/// and messages — all workflow orchestration lives here.
/// </para>
/// <para>
/// Register as <b>scoped</b> so each call session gets its own agent instance
/// with isolated workflow state.
/// </para>
/// </summary>
public class IvrAgent : DelegatingRealtimeAIAgent, IUpdateableRealtimeAgent
{
    private readonly IServiceProvider? _scopedServices;
    private readonly AgentFunctionInvocationMiddleware _delegateFunc;
    private readonly IAgentSessionRegistry _sessionRegistry;
    private readonly List<AITool> _additionalTools;
    private readonly ILogger _logger;

    private const int MaxSessionTokens = 32_000;

    private readonly int _maxSessionTokenCount;
    private readonly SemaphoreSlim _configUpdateLock = new(1, 1);
    private RealtimeIvrStepConfiguration? _currentStepConfig;

    /// <summary>
    /// The pre-configured IVR workflow definition that drives the call flow.
    /// </summary>
    public RealtimeIvrWorkflowDefinition WorkflowDefinition { get; }

    /// <summary>
    /// Live workflow state for the current call session (utterances, turn count,
    /// collected data, current step). Scoped to this agent instance.
    /// </summary>
    public IvrWorkflowState WorkflowState { get; }

    /// <summary>
    /// Raised when a workflow step transition occurs so the transport or
    /// other session components can react (e.g., play hold music, update UI).
    /// </summary>
    public event Func<RealtimeIvrStepConfiguration, CancellationToken, Task>? OnStepTransition;

    public IvrAgent(
        AIAgent innerAgent,
        IAgentSessionRegistry sessionRegistry,
        RealtimeIvrWorkflowDefinition workflowDefinition,
        AgentFunctionInvocationMiddleware? delegateFunc = null,
        IEnumerable<IAIToolCollection>? aIToolCollections = null,
        IServiceProvider? serviceProvider = null,
        ILoggerFactory? loggerFactory = null,
        int maxSessionTokenCount = MaxSessionTokens) : base(innerAgent)
    {
        _sessionRegistry = sessionRegistry;
        _scopedServices = serviceProvider;
        _delegateFunc = delegateFunc ?? DefaultMiddleware;
        _additionalTools = aIToolCollections?.SelectMany(c => c.AsAITools()).ToList() ?? [];
        _maxSessionTokenCount = maxSessionTokenCount;
        _logger = loggerFactory?.CreateLogger<IvrAgent>() ?? NullLogger<IvrAgent>.Instance;
        WorkflowDefinition = workflowDefinition;
        WorkflowState = new IvrWorkflowState
        {
            Status = IvrWorkflowStatus.NotStarted,
            CurrentStepName = workflowDefinition.GetStep(workflowDefinition.InitialStepId)?.Id,
        };
    }

    /// <summary>
    /// Applies a step configuration to the live realtime session, updating
    /// the system prompt and available tools for the current workflow step.
    /// </summary>
    public async Task ApplyStepConfigurationAsync(
        RealtimeIvrStepConfiguration config,
        RealtimeAIAgentSession thread,
        CancellationToken cancellationToken = default)
    {
        await _configUpdateLock.WaitAsync(cancellationToken);
        try
        {
            _currentStepConfig = config;

            _logger.LogInformation(
                "Applying step configuration for step {StepId} with {ToolCount} tools",
                config.StepId,
                config.AvailableTools.Count);

            var sessionOptions = new LiveConversationSessionOptions
            {
                Instructions = config.SystemPrompt,
                Tools = [.. config.AvailableTools]
            };

            await ConfigureSessionAsync(sessionOptions, thread, cancellationToken).ConfigureAwait(false);

            if (OnStepTransition is not null)
            {
                await OnStepTransition(config, cancellationToken);
            }
        }
        finally
        {
            _configUpdateLock.Release();
        }
    }

    /// <summary>
    /// Records a completed utterance (user or agent) into the workflow state.
    /// Call this from the transport's update processing loop when a final
    /// transcript is received.
    /// </summary>
    public void RecordUtterance(ChatRole role, DateTimeOffset turnStart, DateTimeOffset? turnEnd, AIContent transcript)
    {
        try
        {
            WorkflowState.AddUtterance(new RealtimeConversationUtterance(new ChatMessage(role, [transcript]))
            {
                UtteranceStartTime = turnStart,
                UtteranceEndTime = turnEnd
            });
            WorkflowState.TotalTurns++;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording utterance for workflow");
        }
    }

    /// <summary>
    /// Gets the current step configuration, if one has been applied.
    /// </summary>
    public RealtimeIvrStepConfiguration? CurrentStepConfig => _currentStepConfig;

    public Task ConfigureSessionAsync(LiveConversationSessionOptions options, RealtimeAIAgentSession thread, CancellationToken cancellationToken = default)
    {
        return thread.Session.ConfigureSessionAsync(options, cancellationToken);
    }

    public override Task<AgentResponse> RunAsync(IEnumerable<ChatMessage> messages, AgentSession? thread = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        => InnerAgent.RunAsync(messages, thread, AgentRunOptionsWithFunctionMiddleware(options), cancellationToken);

    public override async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? thread = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (thread is not RealtimeAIAgentSession conversationSessionThread)
        {
            throw new ArgumentException("Invalid thread type", nameof(thread));
        }

        var sessionId = conversationSessionThread.ActiveSessionId ?? Id
                        ?? throw new InvalidOperationException("Session ID is required for callbacks");

        Task AgentSessionCallback(IEnumerable<ChatMessage> msgs, CancellationToken ct) => this.SendMessagesToRunAsync(msgs, conversationSessionThread, ct);

        await _sessionRegistry.RegisterSession(sessionId, AgentSessionCallback, cancellationToken).ConfigureAwait(false);

        try
        {
            var runOptions = AgentRunOptionsWithFunctionMiddleware(options);
            await foreach (var update in InnerAgent.RunStreamingAsync(messages, conversationSessionThread, runOptions, cancellationToken))
            {
                yield return update;
            }
        }
        finally
        {
            await _sessionRegistry.UnregisterSession(sessionId, cancellationToken).ConfigureAwait(false);
        }
    }

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(AIAgent) ? this :
        serviceKey is null ? _scopedServices?.GetService(serviceType) : _scopedServices?.GetKeyedService(serviceType, serviceKey) ??
        base.GetService(serviceType, serviceKey);

    private static ValueTask<object?> DefaultMiddleware(AIAgent agent, AIFunctionArguments arguments, AIFunction function, Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> next, CancellationToken ct)
    {
        return next(arguments, ct);
    }

    private RealtimeAgentRunOptions? AgentRunOptionsWithFunctionMiddleware(AgentRunOptions? options)
    {
        var runOptions = options as RealtimeAgentRunOptions ?? new();

        runOptions.SessionOptions ??= new LiveConversationSessionOptions();
        runOptions.SessionOptions.Tools ??= [];

        runOptions.SessionOptions.Tools = [.. ProcessTools(runOptions.SessionOptions.Tools), .. ProcessTools(_additionalTools)];

        IEnumerable<AITool> ProcessTools(IEnumerable<AITool> tools)
        {
            foreach (var tool in tools)
            {
                if (tool is AIFunction funcTool)
                {
                    var authorizedFunc = new AuthorizingAgentFunction(this, funcTool, _delegateFunc);
                    yield return authorizedFunc;
                }
                else
                {
                    yield return tool;
                }
            }
        }

        return runOptions;
    }

    private sealed class AuthorizingAgentFunction : DelegatingAIFunction
    {
        private readonly ILogger<AuthorizingAgentFunction>? _logger;
        private readonly AIAgent _agent;
        private readonly AgentFunctionInvocationMiddleware? _next;

        public readonly List<IToolApprovalRequirement>? ToolRequirements;

        public AuthorizingAgentFunction(AIAgent agent, AIFunction innerFunction, AgentFunctionInvocationMiddleware next) : base(innerFunction)
        {
            _logger = GetService<ILoggerFactory>()?.CreateLogger<AuthorizingAgentFunction>();
            _agent = agent;
            ToolRequirements = innerFunction.UnderlyingMethod?.GetCustomAttributes(true)
                .Where(attr => attr is IToolApprovalRequirementData)
                .SelectMany(attr => ((IToolApprovalRequirementData)attr).GetRequirements())
                .ToList();
            _next = next;
        }

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // Deterministic gate: enforce auth level from workflow state
            if (ToolRequirements is { Count: > 0 }
                && GetService<IToolApprovalHandlerProvider>() is IToolApprovalHandlerProvider provider)
            {
                var invokingIdentity = arguments.Services?.GetService<ClaimsPrincipal>()
                                       ?? GetService<ClaimsPrincipal>();

                var approvalContext = new ToolApprovalContext(
                    this, arguments, _agent, ToolRequirements, invokingIdentity);

                var handlers = await provider.GetHandlersAsync(approvalContext).ConfigureAwait(false);

                foreach (var handler in handlers)
                {
                    await handler.HandleAsync(approvalContext).ConfigureAwait(false);
                }

                if (!approvalContext.HasSucceeded)
                {
                    var failure = new ToolApprovalFailure(
                        InnerFunction,
                        arguments,
                        [.. approvalContext.PendingRequirements],
                        [.. approvalContext.FailureResponses],
                        approvalContext.PendingRequirements is { Count: 0 });

                    _logger?.LogWarning(
                        "Function '{FunctionName}' denied — auth level insufficient for current workflow step.",
                        InnerFunction.Name);

                    return failure.FailureResponseMessage.Text;
                }
            }

            if (_next is not null)
            {
                return await _next.Invoke(_agent, arguments, InnerFunction, base.InvokeCoreAsync, cancellationToken);
            }

            return await base.InvokeCoreAsync(arguments, cancellationToken);
        }
    }
}
