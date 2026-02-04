using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Agents.AI.Extensions.AITools;
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

namespace Agents.AI.Extensions.LiveVoice;

internal class IvrAgent : DelegatingRealtimeAIAgent, IUpdateableRealtimeAgent
{
    private readonly IServiceProvider? _scopedServices;
    private readonly AgentFunctionInvocationMiddleware _delegateFunc;
    private readonly IAgentSessionRegistry _sessionRegistry;
    private readonly List<AITool> _additionalTools;

    // gpt-realtime limit
    private const int MAX_SESSION_TOKENS = 32000;

    private readonly RealtimeIvrWorkflowDefinition _workflow;
    private readonly IvrWorkflowState _state;

    public IvrAgent(
        AIAgent innerAgent,
        IAgentSessionRegistry sessionRegistry,
        AgentFunctionInvocationMiddleware? delegateFunc = null,
        IEnumerable<IAIToolCollection>? aIToolCollections = null,
        IServiceProvider? serviceProvider = null,
        int maxSessionTokenCount = 32000) : base(innerAgent)
    {
        _sessionRegistry = sessionRegistry;
        _scopedServices = serviceProvider;
        _delegateFunc = delegateFunc ?? DefaultMiddleware;
        _additionalTools = aIToolCollections?.SelectMany(c => c.AsAITools()).ToList() ?? [];
        _state = new IvrWorkflowState();
        _workflow = RealtimeIvrWorkflowBuilder.Create("CallerIntentBiometric")
            .WithBasePrompt(prompt => prompt
                .WithRole("financial services contact center agent", "Help callers with account inquiries and service requests")
                .WithPersonality(p => p
                    .Personality("professional, helpful, and efficient")
                    .Tone("warm and confident"))
                .WithContext("You are assisting callers at ACME Financial Services."))

            .WithGreeting(
                "Welcome to ACME Financial Services. I'm an AI assistant here to help you today. How can I assist you?",
                step => step
                    .AddInstruction("Listen for the caller's initial intent")
                    .AddInstruction("If unclear, ask a clarifying question"))

            .AddStep(step => step
                .WithId("2_collect_intent")
                .WithGoal("Understand the caller's primary reason for calling")
                .WithDescription("Collect and confirm the caller's intent")
                .AddInstruction("Categorize the intent: account inquiry, transaction, support, or other")
                .AddInstruction("Confirm understanding by restating the intent briefly")
                
                .AddExample("I understand you're calling about your recent transaction. Let me help you with that.")
                .ExitWhen("Intent is confirmed by caller")
                .TransitionTo("3_verify_identity", "Intent confirmed"))

            .AddStep(step => step
                .WithId("3_verify_identity")
                .WithGoal("Verify the caller's identity before proceeding")
                .WithDescription("Collect identity information for verification")
                .AddInstruction("Ask for the caller's registered name")
                .AddInstruction("Confirm the name before proceeding to voice verification")
                .RequiresAuth(AuthenticationLevel.None)
                .ExitWhen("Caller provides their name")
                .TransitionTo("4_biometric_verification", "Name collected"))

            .AddStep(step => step
                .WithId("4_biometric_verification")
                .WithGoal("Perform voice biometric verification")
                .WithDescription("Guide caller through voice verification process")
                .AddInstruction("Explain that voice verification helps protect their account")
                .AddInstruction("Ask caller to repeat: 'My voice is my password, verify me'")
                .AddInstruction("Wait for biometric tool to complete verification")
                .AddInstruction("If verification fails, inform caller and offer to connect to a representative")
                .RequiresAuth(AuthenticationLevel.AccountVerified)
                // Note: Tools will be resolved from the tool collection at runtime
                .ExitWhen("Voice verification completes successfully or fails")
                .TransitionTo("5_handle_request", "Verification succeeded"))

            .AddStep(step => step
                .WithId("5_handle_request")
                .WithGoal("Process the caller's verified request")
                .WithDescription("Handle the caller's request now that identity is verified")
                .AddInstruction("Access appropriate tools based on the confirmed intent")
                .AddInstruction("Provide clear information about the request status")
                .RequiresAuth(AuthenticationLevel.FullyAuthenticated)
                .ExitWhen("Request is handled or needs escalation"))

            .WithClosing(
                "Is there anything else I can help you with today? Thank you for calling ACME Financial Services.",
                step => step
                    .AddInstruction("Summarize actions taken")
                    .AddInstruction("Provide any reference numbers or next steps"))
            .Build();
    }


    public Task ConfigureSessionAsync(LiveConversationSessionOptions options, LiveConversationAgentSession thread, CancellationToken cancellationToken = default)
    {
        return thread.Session.ConfigureSessionAsync(options, cancellationToken);
    }


    public override Task<AgentRunResponse> RunAsync(IEnumerable<ChatMessage> messages, AgentThread? thread = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        => InnerAgent.RunAsync(messages, thread, AgentRunOptionsWithFunctionMiddleware(options), cancellationToken);

    public override async IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(IEnumerable<ChatMessage> messages, AgentThread? thread = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (thread is not LiveConversationAgentSession conversationSessionThread) throw new ArgumentException("Invalid thread type", nameof(thread));

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

    private static ValueTask<object?> DefaultMiddleware(AIAgent agent, AIFunctionArguments arguments, AIFunction function, Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> next, CancellationToken ct)
    {
        return next(arguments, ct); // Pass through
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
        ;
        return runOptions;
    }


    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(AIAgent) ? this :
        serviceKey == null ? _scopedServices?.GetService(serviceType) : _scopedServices?.GetKeyedService(serviceType, serviceKey) ??
        base.GetService(serviceType, serviceKey);


    internal sealed class AuthorizingAgentFunction : DelegatingAIFunction
    {
        private readonly ILogger<AuthorizingAgentFunction>? _logger;
        private readonly AIAgent _agent;
        private readonly AgentFunctionInvocationMiddleware? _next;
        // used to mark that this function follows the approval workflow
        //private readonly ApprovalRequiredAIFunction? _marker;

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
            //_marker = ToolRequirements is null or { Count: 0 } ? null : new ApprovalRequiredAIFunction(this);
        }

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            //if (ToolRequirements is null or { Count: 0 } || GetService<IToolApprovalHandlerProvider>() is not IToolApprovalHandlerProvider toolApprovalHandlerProvider)
            //{
            //    return await base.InvokeCoreAsync(arguments, cancellationToken);
            //}

            //var invokingIdentity = arguments.Services?.GetService<ClaimsPrincipal>() ?? GetService<ClaimsPrincipal>();
            //var approvalContext = new ToolApprovalContext(this, arguments, _agent, ToolRequirements, invokingIdentity);
            //var handlers = await toolApprovalHandlerProvider.GetHandlersAsync(approvalContext).ConfigureAwait(false);

            //foreach (var handler in handlers)
            //{
            //    await handler.HandleAsync(approvalContext).ConfigureAwait(false);
            //}

            //if (!approvalContext.HasSucceeded)
            //{
            //    var failure = new ToolApprovalFailure(InnerFunction, arguments, [.. approvalContext.PendingRequirements], [.. approvalContext.FailureResponses], approvalContext.PendingRequirements is { Count: 0 });
            //    _logger?.LogWarning("Function '{FunctionName}' invocation denied due to failed tool approval requirements.", InnerFunction.Name);
            //    return failure.FailureResponseMessage.Text;
            //}

            if (_next is not null)
            {
                return await _next.Invoke(_agent, arguments, InnerFunction, base.InvokeCoreAsync, cancellationToken);
            }
            else
            {
                return await base.InvokeCoreAsync(arguments, cancellationToken);
            }
        }

        //public override object? GetService(Type serviceType, object? serviceKey = null) =>
        //    //serviceType == typeof(ApprovalRequiredAIFunction) ? _marker :
        //    //serviceType == typeof(IEnumerable<IToolApprovalRequirement>) ? ToolRequirements :
        //    serviceType.IsInstanceOfType(typeof(AIAgent)) ? _agent :
        //    _agent.GetService(serviceType, serviceKey) ??
        //    base.GetService(serviceType, serviceKey);
    }

}
