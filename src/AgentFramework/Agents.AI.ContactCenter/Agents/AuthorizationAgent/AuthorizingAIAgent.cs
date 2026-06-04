using System.Runtime.CompilerServices;
using System.Security.Claims;
using Agents.AI.Extensions.ToolApproval;
using Agents.AI.Realtime;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Agents.AuthorizationAgent;

public class AuthorizingAIAgent : DelegatingRealtimeAIAgent
{
    private readonly IServiceProvider? _scopedServices;
    private readonly AgentFunctionInvocationMiddleware _delegateFunc;

    public AuthorizingAIAgent(
        RealtimeAIAgent innerAgent,
        AgentFunctionInvocationMiddleware? delegateFunc = null,
        IServiceProvider? serviceProvider = null)
        : base(innerAgent)
    {
        _scopedServices = serviceProvider;
        _delegateFunc = delegateFunc ?? DefaultFunctionMiddleware;
    }


    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in InnerAgent.RunStreamingAsync(messages, session, AgentRunOptionsWithFunctionMiddleware(options), cancellationToken))
        {
            yield return update;
        }
    }

    public override async ValueTask<RealtimeAIAgentSession> CreateSessionAsync(RealtimeSessionOptions? sessionOptions = null, CancellationToken cancellationToken = default)
    {
        var options = RealtimeSessionOptionsWithFunctionMiddleware(sessionOptions);
        return await base.CreateSessionAsync(options, cancellationToken);
    }

    private static ValueTask<object?> DefaultFunctionMiddleware(AIAgent agent, AIFunctionArguments arguments, AIFunction function, Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> next, CancellationToken ct)
    {
        return next(arguments, ct); // Pass through
    }

    private IReadOnlyList<AITool> ApplyToolMiddleware(IEnumerable<AITool> tools)
    {
        var wrappedTools = tools.Select(tool => tool is AIFunction aiFunction && tool is not AuthorizingAgentFunction
        ? new AuthorizingAgentFunction(InnerAgent, aiFunction, _delegateFunc, _scopedServices)
        : tool).ToList();
        return wrappedTools;
    }

    private RealtimeSessionOptions? RealtimeSessionOptionsWithFunctionMiddleware(RealtimeSessionOptions? options)
    {
        if (options?.Tools is null) return options;
        return options.With(tools: ApplyToolMiddleware(options.Tools));
    }

    private AgentRunOptions? AgentRunOptionsWithFunctionMiddleware(AgentRunOptions? options)
    {
        if (options is null || options.GetType() == typeof(AgentRunOptions))
        {
            options = new RealtimeAgentRunOptions()
            {
                ResponseFormat = options?.ResponseFormat,
                AllowBackgroundResponses = options?.AllowBackgroundResponses,
                ContinuationToken = options?.ContinuationToken,
                AdditionalProperties = options?.AdditionalProperties,
            };
        }

        if (options is not RealtimeAgentRunOptions realtimeRunOptions)
        {
            throw new NotSupportedException($"Function Invocation Middleware is only supported without options or with {nameof(RealtimeAgentRunOptions)}.");
        }

        realtimeRunOptions.SessionOptions = RealtimeSessionOptionsWithFunctionMiddleware(realtimeRunOptions.SessionOptions);
        return realtimeRunOptions;
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
        private readonly IServiceProvider? _scopedServices;


        public readonly List<IToolApprovalRequirement>? ToolRequirements;

        public AuthorizingAgentFunction(AIAgent agent, AIFunction innerFunction, AgentFunctionInvocationMiddleware next, IServiceProvider? scopedServices = null) : base(innerFunction)
        {
            _logger = GetService<ILoggerFactory>()?.CreateLogger<AuthorizingAgentFunction>();
            _agent = agent;
            ToolRequirements = innerFunction.UnderlyingMethod?.GetCustomAttributes(true)
                .Where(attr => attr is IToolApprovalRequirementData)
                .SelectMany(attr => ((IToolApprovalRequirementData)attr).GetRequirements())
                .ToList();
            _next = next;
            _scopedServices = scopedServices;
        }

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            if (_scopedServices is not null)
            {
                arguments.Services = _scopedServices;
            }

            if (ToolRequirements is { Count: > 0 })
            {
                var toolApprovalHandlerProvider = arguments.Services?.GetService<IToolApprovalHandlerProvider>()
                    ?? GetService<IToolApprovalHandlerProvider>() ;

                if (toolApprovalHandlerProvider is not null)
                {
                    var invokingIdentity = arguments.Services?.GetService<ClaimsPrincipal>()
                        ?? GetService<ClaimsPrincipal>() as ClaimsPrincipal;
                    var approvalContext = new ToolApprovalContext(this, arguments, _agent, ToolRequirements, invokingIdentity);
                    var handlers = await toolApprovalHandlerProvider.GetHandlersAsync(approvalContext).ConfigureAwait(false);

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
                            "Function '{FunctionName}' invocation denied due to failed tool approval requirements.",
                            InnerFunction.Name);
                        return failure.FailureResponseMessage.Text;
                    }
                }
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
            serviceType == typeof(AIAgent) ? _agent :
            serviceKey == null ? _scopedServices?.GetService(serviceType) : _scopedServices?.GetKeyedService(serviceType, serviceKey) ??
            base.GetService(serviceType, serviceKey);
    }

}
