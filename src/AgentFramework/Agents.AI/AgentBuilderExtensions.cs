using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Agents.AI.RealtimeVoice;
using Azure.Core;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Shared.Diagnostics;

namespace Agents.AI;

public delegate ValueTask<object?> FunctionCoreDelegate(
    FunctionInvocationContext context,
    CancellationToken cancellationToken);

public delegate ValueTask<object?> FunctionMiddlewareDelegate(
    AIAgent agent,
    FunctionInvocationContext context,
    FunctionCoreDelegate next,
    CancellationToken cancellationToken);

public static class AgentBuilderExtensions
{
    public static AIAgentBuilder Use(this AIAgentBuilder builder, Action<AgentRunOptions?> configureRunOptions)
    {
        return builder.Use((innerAgent, _) =>
        {
            return new ConfigureRunOptionsAgent(innerAgent, configureRunOptions);
        }); 
    }


    public static AIAgentBuilder Use(this AIAgentBuilder builder, AgentFunctionInvocationMiddleware callback)
    {
        _ = Throw.IfNull(builder);
        _ = Throw.IfNull(callback);

        return builder.Use((innerAgent, _) =>
        {
            // Function calling requires a ChatClientAgent inner agent.
            if (innerAgent.GetService<FunctionInvokingConversationClient>() is null || innerAgent.GetService<RealtimeAIAgent>() is null)
            {
                throw new InvalidOperationException($"The function invocation middleware can only be used with decorations of a {nameof(AIAgent)} that support usage of FunctionInvokingChatClient decorated chat clients.");
            }

            return new FunctionInvocationRealtimeAgent(innerAgent, callback);
        });
    }



}

