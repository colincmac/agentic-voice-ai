using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Agents.AI.Extensions.AITools;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Agents.AI.Extensions.SessionManagement;
using Agents.AI.Extensions.ToolApproval;
using Agents.AI.RealtimeVoice;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models.CallRecords;

namespace Agents.AI.Extensions.RealtimeAgentHelpers;

public class AuthorizingRealtimeAIAgent : DelegatingRealtimeAIAgent, IUpdateableRealtimeAgent
{
    private readonly IServiceProvider? _scopedServices;
    private readonly AgentFunctionInvocationMiddleware _delegateFunc;
    private readonly IAgentSessionRegistry _sessionRegistry;
    private readonly List<AITool> _additionalTools;

    // gpt-realtime limit
    private const int MAX_SESSION_TOKENS = 32000;
    public AuthorizingRealtimeAIAgent(
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
        //var typedOptions = options as RealtimeAgentRunOptions ?? new();

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

        //runOptions.SessionOptions ??= new LiveConversationSessionOptions();
        //runOptions.SessionOptions.Tools ??= [];
        //runOptions.SessionOptions.Tools = [.. runOptions.SessionOptions.Tools, .. _additionalTools];
        //var originalClientFactory = runOptions.ConversationClientFactory;

        //runOptions.ConversationClientFactory = client =>
        //{
        //    var builder = client.AsBuilder();

        //    if (originalClientFactory is not null)
        //    {
        //        builder.Use(originalClientFactory);
        //    }

        //    return builder.ConfigureOptions(
        //            session =>
        //            {
        //                session.Tools = session.Tools is null ? [] : [.. ProcessTools(session.Tools)];
        //                if (_additionalTools is not null)
        //                {
        //                    session.Tools = [.. session.Tools, .. ProcessTools(_additionalTools)];
        //                }

        //            },
        //            response =>
        //            {

        //                response ??= new();
        //                response.Tools ??= [];
        //                if (_additionalTools is not null)
        //                {
        //                    response.Tools = [.. response.Tools, .. _additionalTools];
        //                }
        //                response.Tools = [.. ProcessTools(response.Tools)];
        //                //response.Tools.Add(AIFunctionFactory.Create(ActivateCreditCardAsync));
        //                //response.Tools.Add(AIFunctionFactory.Create(TransferToHumanAsync));
        //            }
        //        )
        //        .Build(_scopedServices);
        //};

        //IEnumerable<AITool> ProcessTools(IEnumerable<AITool> tools)
        //{
        //    foreach (var tool in tools)
        //    {
        //        if (tool is AIFunction funcTool)
        //        {
        //            var authorizedFunc = new AuthorizingAgentFunction(this, funcTool, _delegateFunc);
        //            yield return authorizedFunc;
        //        }
        //        else
        //        {
        //            yield return tool;
        //        }
        //    }
        //}
        //;
        return runOptions;
    }
    [Description("Activate user credit card information by user pin. Retry ONCE if the user provides incorrect information. Returns true if the user exists.")]
    public Task<bool> ActivateCreditCardAsync([Description("The account ID of the user to look up.")] string accountId, [Description("The users pin provided to them in the letter they recieved with the card")] string userPin, CancellationToken token = default)
    {
        // Implementation for looking up user information
        return Task.FromResult(accountId == "8888" && userPin == "1234");
    }

    [Description("Transfer the user to a human agent.")]
    public Task<string> TransferToHumanAsync(CancellationToken token = default)
    {
        // Implementation for looking up user information
        return Task.FromResult("Escalating to a support agent.");
    }

    [Description("Send a text pin to the user's account phone number.")]
    public Task<string> SendTextPinAsync(CancellationToken token = default)
    {
        // Implementation for looking up user information
        return Task.FromResult("Pin sent: 5555");
    }

    //[Description("Verify pin async.")]
    //public Task<string> SendTextPinAsync(CancellationToken token = default)
    //{
    //    // Implementation for looking up user information
    //    return Task.FromResult("Pin sent: 5555");
    //}

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
            if (ToolRequirements is null or { Count: 0 } || GetService<IToolApprovalHandlerProvider>() is not IToolApprovalHandlerProvider toolApprovalHandlerProvider)
            {
                return await base.InvokeCoreAsync(arguments, cancellationToken);
            }

            var invokingIdentity = arguments.Services?.GetService<ClaimsPrincipal>() ?? GetService<ClaimsPrincipal>();
            var approvalContext = new ToolApprovalContext(this, arguments, _agent, ToolRequirements, invokingIdentity);
            var handlers = await toolApprovalHandlerProvider.GetHandlersAsync(approvalContext).ConfigureAwait(false);

            foreach (var handler in handlers)
            {
                await handler.HandleAsync(approvalContext).ConfigureAwait(false);
            }

            if (!approvalContext.HasSucceeded)
            {
                var failure = new ToolApprovalFailure(InnerFunction, arguments, [.. approvalContext.PendingRequirements], [.. approvalContext.FailureResponses], approvalContext.PendingRequirements is { Count: 0 });
                _logger?.LogWarning("Function '{FunctionName}' invocation denied due to failed tool approval requirements.", InnerFunction.Name);
                return failure.FailureResponseMessage.Text;
            }

            if (_next is not null)
            {
                return await _next.Invoke(_agent, arguments, InnerFunction, base.InvokeCoreAsync, cancellationToken);
            }
            else
            {
                return await base.InvokeCoreAsync(arguments, cancellationToken);
            }
        }

        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            //serviceType == typeof(ApprovalRequiredAIFunction) ? _marker :
            serviceType == typeof(IEnumerable<IToolApprovalRequirement>) ? ToolRequirements :
            serviceType.IsInstanceOfType(typeof(AIAgent)) ? _agent :
            _agent.GetService(serviceType, serviceKey) ??
            base.GetService(serviceType, serviceKey);
    }

}
