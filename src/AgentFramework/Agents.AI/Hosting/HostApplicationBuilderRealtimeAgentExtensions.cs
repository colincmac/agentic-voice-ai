using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Shared.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Extensions.AI.RealtimeVoice;
using Agents.AI.RealtimeVoice;

namespace Agents.AI.Hosting;

/// <summary>
/// Provides extension methods for configuring AI agents in a host application builder.
/// </summary>
public static class HostApplicationBuilderAgentExtensions
{

    public static IHostApplicationBuilder AddRealtimeAIAgent(this IHostApplicationBuilder builder, string name, ILiveConversationClient conversationClient, string? description = null, string? instructions = null)
    {
        Throw.IfNull(builder);
        Throw.IfNullOrEmpty(name);
        Throw.IfNull(conversationClient);
        builder.AddAIAgent(name, (sp, _) => new RealtimeAIAgent(
            client: conversationClient,
            agentOptions: new(
                name: name,
                description: description,
                instructions: instructions),
            sp.GetRequiredService<ILoggerFactory>()));
        return builder;
    }
    public static IHostApplicationBuilder AddRealtimeAIAgent(this IHostApplicationBuilder builder, string name, string? description = null, string? instructions = null, string? liveConversationClientKey = null)
    {
        Throw.IfNull(builder);
        Throw.IfNullOrEmpty(name);
        builder.AddAIAgent(name, (sp, key) => new RealtimeAIAgent(
            client: sp.GeKeyedOrCurrentRequiredService<ILiveConversationClient>(liveConversationClientKey),
            agentOptions: new(
                name: name,
                description: description,
                instructions: instructions),
            sp.GetRequiredService<ILoggerFactory>()));
        return builder;
    }
    public static IHostApplicationBuilder AddRealtimeAIAgent(this IHostApplicationBuilder builder, string name, IConfigurationSection configurationSection, string? liveConversationClientKey = null, Action<RealtimeAgentOptions>? configureOptions = null)
    {
        Throw.IfNull(builder);
        Throw.IfNullOrEmpty(name);
        Throw.IfNull(configurationSection);

        var options = configurationSection.Get<RealtimeAgentOptions>();
        Throw.IfNull(options);
        configureOptions?.Invoke(options);
        options.Name ??= name;
        return builder.AddRealtimeAIAgent(name, options, liveConversationClientKey);
    }

    public static IHostApplicationBuilder AddRealtimeAIAgent(this IHostApplicationBuilder builder, string name, RealtimeAgentOptions options, string? liveConversationClientKey = null)
    {
        Throw.IfNull(builder);
        Throw.IfNull(options);
        Throw.IfNull(name);
        options.Name ??= name;
        builder.AddAIAgent(name, (sp, keyValue) => new RealtimeAIAgent(
            client: sp.GeKeyedOrCurrentRequiredService<ILiveConversationClient>(liveConversationClientKey),
            agentOptions: options,
            sp.GetService<ILoggerFactory>()));

        builder.Services.AddKeyedSingleton<RealtimeAIAgent>(name, (sp, _) =>
        {
            var agent = sp.GetRequiredKeyedService<AIAgent>(name);
            if (agent is not RealtimeAIAgent rtAgent) throw new InvalidOperationException("Could not register realtimeagent");
            return rtAgent;
        });

        builder.Services.AddKeyedSingleton<IRealtimeAIAgent>(name, (sp, _) =>
        {
            var agent = sp.GetRequiredKeyedService<AIAgent>(name);
            if (agent is not RealtimeAIAgent rtAgent) throw new InvalidOperationException("Could not register realtimeagent");
            return rtAgent;
        });
        return builder;
    }
}
