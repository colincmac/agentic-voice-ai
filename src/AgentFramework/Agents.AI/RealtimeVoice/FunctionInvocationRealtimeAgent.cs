using System.Diagnostics;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.AI;

namespace Agents.AI.RealtimeVoice;

public delegate ValueTask<object?> AgentFunctionInvocationMiddleware(
    AIAgent agent,
    AIFunctionArguments arguments,
    AIFunction function,
    Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> next,
    CancellationToken cancellationToken);


public class FunctionInvocationRealtimeAgent : DelegatingRealtimeAIAgent
{
    private readonly AgentFunctionInvocationMiddleware _delegateFunc;

    public FunctionInvocationRealtimeAgent(AIAgent innerAgent, AgentFunctionInvocationMiddleware delegateFunc): base(innerAgent)
    {
        _delegateFunc = delegateFunc;
    }

    public override Task<AgentRunResponse> RunAsync(IEnumerable<ChatMessage> messages, AgentThread? thread = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    => InnerAgent.RunAsync(messages, thread, AgentRunOptionsWithFunctionMiddleware(options), cancellationToken);

    public override IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(IEnumerable<ChatMessage> messages, AgentThread? thread = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        => InnerAgent.RunStreamingAsync(messages, thread, AgentRunOptionsWithFunctionMiddleware(options), cancellationToken);

    private AgentRunOptions? AgentRunOptionsWithFunctionMiddleware(AgentRunOptions? options)
    {
        if (options is null || options.GetType() == typeof(AgentRunOptions))
        {
            options = new RealtimeAgentRunOptions();
        }

        if (options is not RealtimeAgentRunOptions aco)
        {
            throw new NotSupportedException($"Function Invocation Middleware is only supported without options or with {nameof(RealtimeAgentRunOptions)}.");
        }

        var originalFactory = aco.ConversationClientFactory;
        aco.ConversationClientFactory = client =>
        {
            var builder = client.AsBuilder();

            if (originalFactory is not null)
            {
                builder.Use(originalFactory);
            }

            IList<AITool>? WrapTools(IList<AITool>? tools) => tools?.Select(tool => tool is AIFunction aiFunction
                        ? new MiddlewareEnabledFunction(TypedInnerAgent, aiFunction, _delegateFunc.Invoke)
                        : tool).ToList();

            return builder.ConfigureOptions(
                    session  => session.Tools = WrapTools(session.Tools),
                    response => response?.Tools = WrapTools(response.Tools)
                )
                .Build();
        };

        return options;
    }



    private sealed class MiddlewareEnabledFunction(AIAgent innerAgent, AIFunction inner, Func<AIAgent, AIFunctionArguments, AIFunction, Func<AIFunctionArguments, CancellationToken, ValueTask<object?>>, CancellationToken, ValueTask<object?>> next) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            return await next(innerAgent, arguments, InnerFunction, base.InvokeCoreAsync, cancellationToken).ConfigureAwait(false);
        }
    }
}
