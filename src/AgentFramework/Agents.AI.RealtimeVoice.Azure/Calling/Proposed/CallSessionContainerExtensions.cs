using Agents.AI.Extensions.AITools;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.Extensions.SessionManagement;
using Agents.AI.Extensions.ToolApproval;
using Agents.AI.Realtime;
using Agents.AI.RealtimeVoice.Azure.AITools;
using Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Implementation;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Azure.Communication.CallAutomation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed;

// SKETCH — DI wire-up for the new Calling/Proposed shape. Replaces the
// AddConversationHub + ConversationHubBuilder + IContactCenterConversationSessionActivator
// trio with a single AddCallSessionContainer + tier-specific extensions.
//
// Usage:
//
//   builder.AddCallSessionContainer()
//       .AddRealtimeVoiceStrategy()      // Tier 0 — wraps AuthorizingRealtimeAIAgent
//       .AddDtmfStrategy()               // Tier 4 — requires a registered ISpeechSynthesizer
//       .AddDashboardProjectionObserver();
//
// Followed by:
//   app.MapCallAutomation();            // updated CallingApi that uses ICallSessionFactory

public static class CallSessionContainerExtensions
{
    /// <summary>
    /// Registers the singleton call container: registry, factory, in-memory quality
    /// reporter, plus the realtime strategy adapter wiring as a transient that
    /// resolves the agent stack at session-create time.
    /// </summary>
    public static CallSessionContainerBuilder AddCallSessionContainer(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        services.TryAddSingleton<CallSessionRegistry>();
        services.TryAddSingleton<ICallSessionRegistry>(sp => sp.GetRequiredService<CallSessionRegistry>());

        services.TryAddSingleton<InMemoryCallQualityReporter>();
        services.TryAddSingleton<ICallQualityReporter>(sp => sp.GetRequiredService<InMemoryCallQualityReporter>());

        services.TryAddScoped<CallSessionAccessor>();
        services.TryAddScoped<ICallSessionAccessor>(sp => sp.GetRequiredService<CallSessionAccessor>());

        services.TryAddSingleton<ICallSessionFactory, CallSessionFactory>();

        return new CallSessionContainerBuilder(builder);
    }
}

public sealed class CallSessionContainerBuilder
{
    public CallSessionContainerBuilder(IHostApplicationBuilder builder)
    {
        Builder = builder;
    }

    public IHostApplicationBuilder Builder { get; }

    public IServiceCollection Services => Builder.Services;

    /// <summary>
    /// Registers the Tier 0 realtime voice strategy. Resolves the production
    /// <see cref="AuthorizingRealtimeAIAgent"/> at session-create time and wraps
    /// it in <see cref="AuthorizingAgentRealtimeBackend"/>.
    /// </summary>
    /// <param name="realtimeAgentServiceKey">
    /// Optional keyed-service key for the underlying <see cref="RealtimeAIAgent"/>.
    /// When set, resolves the agent registered under that key (e.g. <c>"TriageAgent"</c>).
    /// When null, resolves the unkeyed <see cref="RealtimeAIAgent"/>.
    /// </param>
    public CallSessionContainerBuilder AddRealtimeVoiceStrategy(
        string? realtimeAgentServiceKey = null,
        RealtimeAgentRunOptions? runOptions = null,
        AgentFunctionInvocationMiddleware? middlewareOverride = null)
    {
        Services.TryAddScoped<IAgentSessionRegistry, AgentSessionRegistry>();
        Services.TryAddSingleton<IToolApprovalStore, InMemoryToolApprovalStore>();
        Services.TryAddScoped<IToolApprovalHandlerProvider, ToolApprovalHandlerProvider>();

        Services.TryAddScoped(sp =>
        {
            var agent = !string.IsNullOrEmpty(realtimeAgentServiceKey)
                ? sp.GetRequiredKeyedService<RealtimeAIAgent>(realtimeAgentServiceKey)
                : sp.GetRequiredService<RealtimeAIAgent>();

            var registry = sp.GetRequiredService<IAgentSessionRegistry>();
            var toolCollections = sp.GetServices<IAIToolCollection>();

            return new AuthorizingRealtimeAIAgent(
                agent,
                registry,
                delegateFunc: middlewareOverride,
                toolCollections,
                sp);
        });

        Services.AddTransient<IRealtimeVoiceBackend>(sp =>
        {
            var agent = sp.GetRequiredService<AuthorizingRealtimeAIAgent>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new AuthorizingAgentRealtimeBackend(agent, runOptions: runOptions, loggerFactory);
        });
        Services.AddSingleton<IConversationStrategyFactory, RealtimeVoiceStrategyFactory>();
        return this;
    }

    /// <summary>
    /// Registers the singleton <see cref="CallAutomationClient"/> using the
    /// connection string from <see cref="CommunicationOptions"/>. Required for the
    /// ACS-bridged caller path.
    /// </summary>
    public CallSessionContainerBuilder AddAcsCallAutomation()
    {
        Services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CommunicationOptions>>();
            return new CallAutomationClient(options.Value.Acs.ConnectionString);
        });
        return this;
    }

    /// <summary>
    /// Registers the Tier 4 DTMF strategy. Requires an <see cref="Agents.AI.Extensions.LiveVoice.Media.Audio.ISpeechSynthesizer"/>
    /// to be registered separately for prompt playback.
    /// </summary>
    public CallSessionContainerBuilder AddDtmfStrategy(bool useStreaming = true)
    {
        if (useStreaming)
        {
            Services.AddSingleton<IConversationStrategyFactory, DtmfStreamingStrategyFactory>();
        }
        else
        {
            Services.AddSingleton<IConversationStrategyFactory, DtmfVerbStrategyFactory>();
        }
        return this;
    }

    /// <summary>
    /// Registers the verb-based DTMF strategy. Pairs with
    /// <see cref="AcsCallAutomationEdge"/> and emits SpeakText + CollectDtmf
    /// directives instead of locally synthesized PCM. Requires no
    /// <see cref="Agents.AI.Extensions.LiveVoice.Media.Audio.ISpeechSynthesizer"/>
    /// since the platform handles TTS via attached Cognitive Services.
    /// </summary>
    public CallSessionContainerBuilder AddDtmfVerbStrategy()
    {
        Services.AddSingleton<IConversationStrategyFactory, DtmfVerbStrategyFactory>();
        return this;
    }

    /// <summary>
    /// Registers the default <see cref="DashboardProjectionObserver"/> so dashboard
    /// snapshots are populated from <see cref="StrategyEvent"/>s.
    /// </summary>
    public CallSessionContainerBuilder AddDashboardProjectionObserver()
    {
        Services.AddSingleton<ICallObserver, DashboardProjectionObserver>();
        return this;
    }

    /// <summary>
    /// Registers <see cref="CallControlTools"/> as a scoped <see cref="IAIToolCollection"/>
    /// so the realtime agent can hang up or transfer the live call. Resolves the
    /// scoped <see cref="ICallSessionAccessor"/> bound by <c>CallSessionFactory</c>,
    /// so this only works inside the per-call DI scope.
    /// </summary>
    public CallSessionContainerBuilder AddCallControlTools()
    {
        Services.AddScoped<IAIToolCollection, CallControlTools>();
        return this;
    }
}
