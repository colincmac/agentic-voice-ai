using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.RealtimeVoice;

public static class RealtimeVoiceClientExtensions
{
    /// <summary>
    /// Creates a new <see cref="RealtimeAIAgent"/> instance.
    /// </summary>
    public static RealtimeAIAgent CreateAIAgent(
        this ILiveConversationClient conversationClient,
        RealtimeAgentOptions? agentOptions = null,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? services = null) =>
        new(
            conversationClient,
            agentOptions: agentOptions,
            loggerFactory: loggerFactory,
            services: services);


    internal static ILiveConversationClient WithDefaultAgentMiddleware(this ILiveConversationClient conversationClient, RealtimeAgentOptions? options = null, IServiceProvider? services = null)
    {
        var conversationClientBuilder = conversationClient.AsBuilder();

        if (conversationClient.GetService<FunctionInvokingConversationClient>() is null)
        {
            _ = conversationClientBuilder.Use((innerClient, services) =>
            {
                var loggerFactory = services.GetService<ILoggerFactory>();

                return new FunctionInvokingConversationClient(innerClient, loggerFactory, services);
            });
        }

        var agentConversationClient = conversationClientBuilder.Build(services);

        return agentConversationClient;
    }
}
