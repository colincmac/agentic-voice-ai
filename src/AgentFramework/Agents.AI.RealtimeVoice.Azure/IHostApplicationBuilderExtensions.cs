using Agents.AI.Extensions;
using Agents.AI.Extensions.AITools;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.LiveVoice.Media;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.Extensions.SessionManagement;
using Agents.AI.Extensions.ToolApproval;
using Agents.AI.Realtime;
using Agents.AI.RealtimeVoice.Azure.Authorization.VoiceApproval;
using Agents.AI.RealtimeVoice.Azure.Calling;
using Agents.AI.RealtimeVoice.Azure.Calling.Routing;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Agents.AI.RealtimeVoice.Azure.Media;
using Agents.AI.RealtimeVoice.Azure.Monitoring;
using Azure.Communication.CallAutomation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agents.AI.RealtimeVoice.Azure;

public static class IHostApplicationBuilderExtensions
{
    public static ConversationHubBuilder AddConversationHub(this IHostApplicationBuilder builder, string comunicationOptionsSectionKey = CommunicationOptions.SectionName, string contactCenterOptionsSectionKey = ContactCenterOptions.SectionName)
    {
        var communicationsSection = builder.Configuration.GetRequiredSection(comunicationOptionsSectionKey);
        var contactCenterSection = builder.Configuration.GetRequiredSection(contactCenterOptionsSectionKey);
        return builder.AddConversationHub(communicationsSection, contactCenterSection);
    }

    public static ConversationHubBuilder AddConversationHub(this IHostApplicationBuilder builder, IConfigurationSection communicationsSection, IConfigurationSection contactCenterSection)
    {
        return builder.AddConversationHub(communicationsSection.Bind, contactCenterSection.Bind);
    }

    public static ConversationHubBuilder AddConversationHub(
        this IHostApplicationBuilder builder,
        Action<CommunicationOptions> configureCommunicationOptions,
        Action<ContactCenterOptions> configureContactCenterOptions)
    {
        builder.Services.Configure(configureCommunicationOptions);
        builder.Services.Configure(configureContactCenterOptions);
        builder.Services.AddMemoryCache();
        // Note: AddIvrWorkflowServices has been removed. Use the new RealtimeIvrWorkflowCoordinator pattern instead.
        // Register ConversationHub as singleton

        builder.Services.AddSingleton<ContactCenterConversationHub>();

        // Register routing strategy (swappable for conference, hold-aware, etc.)
        builder.Services.TryAddSingleton<ISessionRouter, BroadcastSessionRouter>();

        // Register centralized telemetry
        builder.Services.TryAddSingleton<SessionTelemetry>();

        // Register LiveCallRegistry for operator dashboard
        builder.Services.AddSingleton<LiveCallRegistry>();
        builder.Services.AddSingleton<ILiveCallRegistry>(sp => sp.GetRequiredService<LiveCallRegistry>());

        builder.Services.AddHostedService(provider => provider.GetRequiredService<ContactCenterConversationHub>());
        builder.Services.TryAddSingleton<IContactCenterConversationSessionActivator, DefaultContactCenterConversationSessionActivator>();
        builder.Services.TryAddSingleton<IToolApprovalStore, InMemoryToolApprovalStore>();

        // Register session-scoped services (created per session)   
        builder.Services.AddScoped<IAgentSessionRegistry, AgentSessionRegistry>();
        builder.Services.AddScoped<IToolApprovalHandlerProvider, ToolApprovalHandlerProvider>();
        builder.Services.AddScoped<VoiceApprovalStore>();
        builder.Services.AddScoped<IToolApprovalHandler, VoiceApprovalHandler>();
        builder.Services.AddScoped((sp) =>
        {
            var optionsMonitor = sp.GetRequiredService<IOptionsMonitor<ContactCenterOptions>>();
            var options = optionsMonitor.CurrentValue;
            var agent = !string.IsNullOrEmpty(options.RealtimeAgentServiceKey)
                ? sp.GetRequiredKeyedService<RealtimeAIAgent>(options.RealtimeAgentServiceKey)
                : sp.GetRequiredService<RealtimeAIAgent>();

            var registry = sp.GetRequiredService<IAgentSessionRegistry>();
            var toolCollections = sp.GetServices<IAIToolCollection>();
            return new AuthorizingRealtimeAIAgent(agent, registry, options.AgentFunctionInvocationMiddleware, toolCollections, sp);
        });

        // Register protocol adapters if needed
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddMeter(ConversationSessionActivitySource.MeterName))
            .WithTracing(tracing => tracing.AddSource(ConversationSessionActivitySource.ActivitySourceName));

        builder.Services.AddSingleton<WebSocketResourceManager>();


        return new ConversationHubBuilder(builder);
    }
}
