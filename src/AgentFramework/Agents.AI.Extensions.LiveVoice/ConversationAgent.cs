//using System;
//using System.Collections.Generic;
//using System.ComponentModel;
//using System.Runtime.CompilerServices;
//using System.Security.Claims;
//using System.Text;
//using System.Text.Json;
//using Agents.AI.Extensions.AITools;
//using Agents.AI.Extensions.RealtimeAgentHelpers;
//using Agents.AI.Extensions.SessionManagement;
//using Agents.AI.Extensions.ToolApproval;
//using Agents.AI.RealtimeVoice;
//using DnsClient.Internal;
//using Extensions.AI.Contents;
//using Extensions.AI.RealtimeVoice;
//using Microsoft.Agents.AI;
//using Microsoft.Extensions.AI;
//using Microsoft.Extensions.DependencyInjection;
//using Microsoft.Extensions.Logging;

//namespace Agents.AI.Extensions.LiveVoice;

//internal class ConversationAgent : DelegatingRealtimeAIAgent, IUpdateableRealtimeAgent
//{
//    private readonly IServiceProvider? _scopedServices;
//    private readonly AgentFunctionInvocationMiddleware _delegateFunc;
//    private readonly IAgentSessionRegistry _sessionRegistry;
//    private readonly List<AITool>? _additionalTools;
//    internal const string FunctionPrefix = "transition_to_";

//    private static readonly JsonElement handoffSchema = AIFunctionFactory.Create(
//        ([Description("The reason for the handoff")] string? reasonForHandoff) => { }).JsonSchema;
//    // gpt-realtime limit
//    private const int MAX_SESSION_TOKENS = 32000;
//    public ConversationAgent(
//        AIAgent innerAgent,
//        IAgentSessionRegistry sessionRegistry,
//        AgentFunctionInvocationMiddleware? delegateFunc = null,
//        IEnumerable<IAIToolCollection>? aIToolCollections = null,
//        IServiceProvider? serviceProvider = null,
//        int maxSessionTokenCount = 32000) : base(innerAgent)
//    {
//        _sessionRegistry = sessionRegistry;
//        _scopedServices = serviceProvider;
//        _delegateFunc = delegateFunc ?? DefaultMiddleware;
//        _additionalTools = aIToolCollections?.SelectMany(c => c.AsAITools()).ToList();
//    }


//    public Task ConfigureSessionAsync(LiveConversationSessionOptions options, LiveConversationAgentSession thread, CancellationToken cancellationToken = default)
//    {
//        return thread.Session.ConfigureSessionAsync(options, cancellationToken);
//    }

//    public override Task<AgentRunResponse> RunAsync(IEnumerable<ChatMessage> messages, AgentThread? thread = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
//        => InnerAgent.RunAsync(messages, thread, AgentRunOptionsWithFunctionMiddleware(options), cancellationToken);

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

//}
