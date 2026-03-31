using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.Realtime;

public class FunctionInvokingRealtimeAIAgent(RealtimeAIAgent innerAgent) : DelegatingRealtimeAIAgent(innerAgent)
{

    private readonly Func<AIAgent, FunctionInvocationContext, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>, CancellationToken, ValueTask<object?>> _delegateFunc;
    // Decorate options to add the middleware function
    private AgentRunOptions? AgentRunOptionsWithFunctionMiddleware(AgentRunOptions? options)
    {
        if (options is null || options.GetType() == typeof(AgentRunOptions))
        {
            options = new ChatClientAgentRunOptions()
            {
                ResponseFormat = options?.ResponseFormat,
                AllowBackgroundResponses = options?.AllowBackgroundResponses,
                ContinuationToken = options?.ContinuationToken,
                AdditionalProperties = options?.AdditionalProperties,
            };
        }

        if (options is not ChatClientAgentRunOptions aco)
        {
            throw new NotSupportedException($"Function Invocation Middleware is only supported without options or with {nameof(ChatClientAgentRunOptions)}.");
        }

        var originalFactory = aco.ChatClientFactory;
        aco.ChatClientFactory = chatClient =>
        {
            var builder = chatClient.AsBuilder();

            if (originalFactory is not null)
            {
                builder.Use(originalFactory);
            }

            return builder.ConfigureOptions(co
                => co.Tools = co.Tools?.Select(tool => tool is AIFunction aiFunction
                        ? new MiddlewareEnabledFunction(this.InnerAgent, aiFunction, this._delegateFunc)
                        : tool)
                    .ToList())
                .Build();
        };

        return options;
    }

    private sealed class MiddlewareEnabledFunction(AIAgent innerAgent, AIFunction innerFunction, Func<AIAgent, FunctionInvocationContext, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>, CancellationToken, ValueTask<object?>> next) : DelegatingAIFunction(innerFunction)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var context = FunctionInvokingChatClient.CurrentContext
                ?? new FunctionInvocationContext() // When there is no ambient context, create a new one to hold the arguments
                {
                    Arguments = arguments,
                    Function = this.InnerFunction,
                    CallContent = new(string.Empty, this.InnerFunction.Name, new Dictionary<string, object?>(arguments)),
                };

            return await next(innerAgent, context, CoreLogicAsync, cancellationToken).ConfigureAwait(false);

            ValueTask<object?> CoreLogicAsync(FunctionInvocationContext ctx, CancellationToken cancellationToken)
                => base.InvokeCoreAsync(ctx.Arguments, cancellationToken);
        }
    }
}
