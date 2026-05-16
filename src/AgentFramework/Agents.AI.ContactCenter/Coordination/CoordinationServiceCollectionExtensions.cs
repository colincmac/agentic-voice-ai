using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination.Implementation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Agents.AI.ContactCenter.Coordination;

/// <summary>
/// DI helpers for the hyperscale coordination plane. These primitives are
/// shared by both in-memory (dev) and distributed (prod) call-state paths;
/// <see cref="IClusterIdentity"/> in particular is required by call
/// telemetry tags (ADR-0004) and is therefore always registered.
/// </summary>
public static class CoordinationServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="HyperscaleOptions"/> from the
    /// <see cref="HyperscaleOptions.SectionName"/> configuration section and
    /// registers the singleton <see cref="IClusterIdentity"/>. Safe to call
    /// multiple times.
    /// </summary>
    public static IHostApplicationBuilder AddClusterIdentity(this IHostApplicationBuilder builder)
    {
        return builder.AddClusterIdentity(builder.Configuration.GetSection(HyperscaleOptions.SectionName));
    }

    /// <summary>
    /// Binds <see cref="HyperscaleOptions"/> from the supplied configuration
    /// section and registers the singleton <see cref="IClusterIdentity"/>.
    /// </summary>
    public static IHostApplicationBuilder AddClusterIdentity(this IHostApplicationBuilder builder, IConfigurationSection hyperscaleSection)
    {
        builder.Services.Configure<HyperscaleOptions>(hyperscaleSection);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IClusterIdentity, HostClusterIdentity>();
        return builder;
    }

    /// <summary>
    /// Registers the in-process <see cref="IWebhookIdempotencyStore"/>
    /// (<see cref="InMemoryWebhookIdempotencyStore"/>). Suitable for dev /
    /// Aspire and for the per-pod fallback in ADR-0004's degraded-mode
    /// admission contract.
    /// </summary>
    public static IHostApplicationBuilder AddInMemoryWebhookIdempotencyStore(this IHostApplicationBuilder builder)
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<IWebhookIdempotencyStore, InMemoryWebhookIdempotencyStore>();
        return builder;
    }

    /// <summary>
    /// Registers the Redis-backed <see cref="IWebhookIdempotencyStore"/>
    /// (<see cref="RedisWebhookIdempotencyStore"/>). The caller is responsible
    /// for registering an <see cref="StackExchange.Redis.IConnectionMultiplexer"/>
    /// (typically via Aspire's <c>AddRedisClient</c>).
    /// </summary>
    public static IHostApplicationBuilder AddRedisWebhookIdempotencyStore(this IHostApplicationBuilder builder)
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<IWebhookIdempotencyStore, RedisWebhookIdempotencyStore>();
        return builder;
    }
}
