using Agents.AI.Extensions.AITools;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.Extensions.SessionManagement;
using Agents.AI.Extensions.ToolApproval;
using Agents.AI.Realtime;
using Agents.AI.ContactCenter.AITools;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Telemetry;
using Agents.AI.ContactCenter.Configuration;
using Azure.Communication.CallAutomation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Agents.AI.ContactCenter.Calling.Strategies.Composite;
using Agents.AI.ContactCenter.Calling.Strategies.Nlu;
using Agents.AI.ContactCenter.Calling.Strategies.Dtmf;
using Agents.AI.ContactCenter.Calling.Strategies.RealtimeVoice;
using Agents.AI.ContactCenter.Calling.Core;

namespace Agents.AI.ContactCenter.Calling;

// SKETCH — DI wire-up for the new Calling/Proposed shape. Replaces the
// AddConversationHub + ConversationHubBuilder + IContactCenterConversationSessionActivator
// trio with a single AddCallSessionContainer + tier-specific extensions.
//
// Usage:
//
//   builder.AddCallSessionContainer()
//       .AddRealtimeVoiceStrategy()      // Tier 0 — wraps AuthorizingRealtimeAIAgent
//       .AddDtmfStreamingStrategy()      // Tier 4 (streaming edge) — requires a registered ISpeechSynthesizer
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
    public static CallSessionContainerBuilder AddCallSessionContainer(this IHostApplicationBuilder builder, string communicationOptionsSectionName = CommunicationOptions.SectionName)
    {
        return builder.AddCallSessionContainer(builder.Configuration.GetSection(communicationOptionsSectionName));
    }

    public static CallSessionContainerBuilder AddCallSessionContainer(this IHostApplicationBuilder builder, IConfigurationSection communicationOptionsSection)
    {

        builder.Services.Configure<CommunicationOptions>(communicationOptionsSection);

        return builder.AddCallSessionContainerCore();
    }

    public static CallSessionContainerBuilder AddCallSessionContainer(this IHostApplicationBuilder builder, CommunicationOptions communicationOptions)
    {

        builder.Services.Configure<CommunicationOptions>(options =>
        {
            options = communicationOptions;
        });

        return builder.AddCallSessionContainerCore();
    }


    /// <summary>
    /// Registers the dedicated <see cref="CallingTelemetry"/> singleton for the
    /// new Calling/Proposed stack and wires its <see cref="System.Diagnostics.ActivitySource"/>
    /// / <see cref="System.Diagnostics.Metrics.Meter"/> into the host's
    /// OpenTelemetry pipeline. Safe to call multiple times.
    /// </summary>
    public static IHostApplicationBuilder AddCallSessionContainerTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton<CallingTelemetry>();

        builder.Services
            .AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddMeter(CallingActivitySource.MeterName))
            .WithTracing(tracing => tracing.AddSource(CallingActivitySource.ActivitySourceName));

        return builder;
    }

    private static CallSessionContainerBuilder AddCallSessionContainerCore(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        builder.AddClusterIdentity();

        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CommunicationOptions>>();
            return new CallAutomationClient(options.Value.Acs.ConnectionString);
        });

        services.TryAddSingleton<CallSessionRegistry>();
        services.TryAddSingleton<ICallSessionRegistry>(sp => sp.GetRequiredService<CallSessionRegistry>());

        services.TryAddSingleton<InMemoryCallQualityReporter>();
        services.TryAddSingleton<ICallQualityReporter>(sp => sp.GetRequiredService<InMemoryCallQualityReporter>());

        services.TryAddScoped<CallSessionAccessor>();
        services.TryAddScoped<ICallSessionAccessor>(sp => sp.GetRequiredService<CallSessionAccessor>());

        services.TryAddSingleton<ICallSessionFactory, CallSessionFactory>();

        builder.AddCallSessionContainerTelemetry();
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
    /// Registers the streaming DTMF strategy. Pairs with
    /// <see cref="AcsCallerStreamEdge"/> and emits locally synthesized PCM through the
    /// bidirectional media WebSocket. Requires an <see cref="Media.Audio.ISpeechSynthesizer"/>
    /// to be registered separately for prompt playback.
    /// </summary>
    public CallSessionContainerBuilder AddDtmfStreamingStrategy()
    {
        Services.AddSingleton<IConversationStrategyFactory, DtmfStreamingStrategyFactory>();
        return this;
    }

    /// <summary>
    /// Registers the verb-based DTMF strategy. Pairs with
    /// <see cref="AcsCallAutomationEdge"/> and emits SpeakText + CollectDtmf
    /// directives instead of locally synthesized PCM. Requires no
    /// <see cref="Media.Audio.ISpeechSynthesizer"/>
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

    /// <summary>
    /// Registers the Tier 3 NLU strategy (<see cref="NluConversationStrategy"/>). Requires an
    /// <see cref="Agents.AI.ContactCenter.Agents.IntentAgent.IvrIntentAgent"/> (which owns
    /// speech recognition + JSON intent classification) and
    /// <see cref="Media.Audio.ISpeechSynthesizer"/> to be registered separately.
    /// </summary>
    public CallSessionContainerBuilder AddNluStrategy()
    {
        Services.AddSingleton<IConversationStrategyFactory, NluConversationStrategyFactory>();
        return this;
    }

    /// <summary>
    /// Registers a transfer escalation target so strategies that emit
    /// <see cref="OutboundDirective.TransferCall"/> have a default destination
    /// (e.g. NLU's <c>transfer_to_agent</c> intent, or DTMF "press 0 for agent").
    /// </summary>
    public CallSessionContainerBuilder AddTransferEscalationTarget(string targetIdentifier, TransferKind kind = TransferKind.BlindToPhoneNumber)
    {
        Services.AddSingleton(new TransferEscalationTarget(targetIdentifier, kind));
        return this;
    }

    /// <summary>
    /// Registers a <see cref="CompositeFallbackStrategy"/> at <paramref name="topTier"/>. The composite
    /// shadows any individual factory at the top tier (last-registered wins) and walks the
    /// <paramref name="orderedTiers"/> chain on each inner strategy fault, preserving
    /// <see cref="IvrWorkflowState"/> via <c>restoreFrom</c>. Per-call scoped services
    /// (e.g. <c>CallerAuthenticationState</c>) are shared across every tier in the chain.
    /// </summary>
    /// <param name="topTier">
    /// The tier the call session factory will look up. Must be the first entry of
    /// <paramref name="orderedTiers"/>.
    /// </param>
    /// <param name="orderedTiers">
    /// Ordered fallback chain — first tier is the primary; subsequent tiers are tried in order
    /// when the active inner faults.
    /// </param>
    /// <remarks>
    /// Register the inner factories (e.g. <see cref="AddRealtimeVoiceStrategy"/>,
    /// <see cref="AddNluStrategy"/>, <see cref="AddDtmfStreamingStrategy"/>,
    /// <see cref="AddDtmfVerbStrategy"/>) BEFORE calling this
    /// method so the composite can resolve them at call-create time.
    /// </remarks>
    public CallSessionContainerBuilder AddCompositeFallbackStrategy(AgentTier topTier, params AgentTier[] orderedTiers)
    {
        if (orderedTiers is null || orderedTiers.Length == 0)
        {
            throw new ArgumentException("Provide at least one tier in the fallback chain.", nameof(orderedTiers));
        }
        Services.AddSingleton<IConversationStrategyFactory>(_ =>
            new CompositeFallbackStrategyFactory(topTier, orderedTiers));
        return this;
    }

    /// <summary>
    /// Wires the hybrid sticky-WS + stateless-webhook ownership router per
    /// ADR-0011 onto the call container. Registers an
    /// <see cref="ICallOwnershipDirectory"/>, an <see cref="IWebhookForwarder"/>,
    /// an <see cref="IPodHeartbeat"/>, and an <see cref="IWebhookIdempotencyStore"/>
    /// so the call-edge can claim ownership on answer, look up the owner on
    /// every mid-call callback, forward to the WS-owning pod when remote, and
    /// release the lease on <c>CallDisconnected</c>.
    /// </summary>
    /// <param name="useRedis">
    /// When <c>true</c> (the default in-cluster posture) wires the Redis-backed
    /// flavour of every primitive plus the <c>HttpWebhookForwarder</c>. The
    /// caller must register an <see cref="StackExchange.Redis.IConnectionMultiplexer"/>
    /// (typically via Aspire's <c>AddRedisClient</c>) and set the
    /// <c>WebhookForwarder</c> section of <see cref="HyperscaleOptions"/>.
    /// When <c>false</c>, wires the in-memory flavour suitable for the showcase
    /// / single-pod dev loop; the <see cref="NullWebhookForwarder"/> resolves
    /// every owner as either local (no-op) or unreachable (drop to the local
    /// fallback path).
    /// </param>
    /// <remarks>
    /// Idempotent — every inner registration uses <c>TryAddSingleton</c>, so
    /// callers may safely co-register the Redis and in-memory flavours of
    /// individual primitives ahead of this call (e.g. Redis ownership +
    /// in-memory idempotency for the pilot tier).
    /// </remarks>
    public CallSessionContainerBuilder AddCallOwnershipRouting(bool useRedis = true)
    {
        if (useRedis)
        {
            Builder.AddRedisCallOwnershipDirectory();
            Builder.AddRedisWebhookIdempotencyStore();
            Builder.AddRedisPodHeartbeat();
            Builder.AddHttpWebhookForwarder();
        }
        else
        {
            Builder.AddInMemoryCallOwnershipDirectory();
            Builder.AddInMemoryWebhookIdempotencyStore();
            Builder.AddInMemoryPodHeartbeat();
            Builder.AddInMemoryWebhookForwarder();
        }

        return this;
    }
}
