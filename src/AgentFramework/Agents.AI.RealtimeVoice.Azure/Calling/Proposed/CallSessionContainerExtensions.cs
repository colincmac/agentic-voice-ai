using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.RealtimeVoice.Azure.Calling.Proposed;
using Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Implementation;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    public CallSessionContainerBuilder AddRealtimeVoiceStrategy()
    {
        Services.AddTransient<IRealtimeVoiceBackend>(sp =>
        {
            var agent = sp.GetRequiredService<AuthorizingRealtimeAIAgent>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new AuthorizingAgentRealtimeBackend(agent, runOptions: null, loggerFactory);
        });
        Services.AddSingleton<IConversationStrategyFactory, Implementation.RealtimeVoiceStrategyFactory>();
        return this;
    }

    /// <summary>
    /// Registers the Tier 4 DTMF strategy. Requires an <see cref="Agents.AI.Extensions.LiveVoice.Media.Audio.ISpeechSynthesizer"/>
    /// to be registered separately for prompt playback.
    /// </summary>
    public CallSessionContainerBuilder AddDtmfStrategy()
    {
        Services.AddSingleton<IConversationStrategyFactory, Implementation.DtmfStrategyFactory>();
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
}
