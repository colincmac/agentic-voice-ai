using Agents.AI.ContactCenter.IvrWorkflow.Tools;
using Microsoft.Extensions.AI;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI extensions for registering <see cref="AIFunction"/> instances against an
/// <see cref="IIvrToolRegistry"/> keyed by the realtime agent's service key.
/// </summary>
/// <remarks>
/// <para>
/// The registry is keyed by <c>agentKey</c> so a host can wire multiple
/// realtime agents on the same process, each with its own tool surface.
/// The realtime call-workflow strategy resolves <see cref="IIvrToolRegistry"/>
/// using the same key it uses to resolve the agent, mirroring the Microsoft
/// Agent Framework convention where tools belong to the agent.
/// </para>
/// <para>
/// Re-registering the same <paramref name="name"/> overwrites the prior entry
/// (last-wins), preserving the behaviour of the legacy
/// <c>AddNamedAIFunction</c> helper that this API replaces.
/// </para>
/// </remarks>
public static class IvrToolServiceCollectionExtensions
{
    /// <summary>
    /// Register an <see cref="AIFunction"/> under <paramref name="name"/> for the
    /// realtime agent identified by <paramref name="agentKey"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="agentKey">DI service key shared with the realtime agent registration.</param>
    /// <param name="name">Tool lookup name referenced from workflow blueprints.</param>
    /// <param name="factory">Factory invoked per call to materialize the function from the call scope.</param>
    /// <param name="lifetime">
    /// Descriptive hint:
    /// <see cref="ServiceLifetime.Singleton"/> for stateless tools,
    /// <see cref="ServiceLifetime.Scoped"/> for tools that capture per-call state
    /// (e.g. <c>CallerAuthenticationState</c>, <c>ICallSessionAccessor</c>),
    /// <see cref="ServiceLifetime.Transient"/> when each materialization should produce a fresh instance.
    /// The registry never caches results; per-call caching lives on
    /// <see cref="Agents.AI.ContactCenter.IvrWorkflow.Execution.CallWorkflowSession"/>.
    /// </param>
    public static IServiceCollection AddIvrTool(
        this IServiceCollection services,
        string agentKey,
        string name,
        Func<IServiceProvider, AIFunction> factory,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(agentKey);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(factory);

        services.AddIvrToolRegistry(agentKey);

        var existing = FindOrAddBuilderDescriptor(services, agentKey);
        existing.Add(new ToolBinding(
            name,
            lifetime,
            sp => factory(sp) ?? throw new InvalidOperationException(
                $"Factory for IVR tool '{name}' (agent '{agentKey}') returned null.")));

        return services;
    }

    /// <summary>
    /// Convenience overload that registers an already-built <paramref name="function"/>
    /// as a singleton binding under <paramref name="name"/>.
    /// </summary>
    public static IServiceCollection AddIvrTool(
        this IServiceCollection services,
        string agentKey,
        string name,
        AIFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return services.AddIvrTool(agentKey, name, _ => function, ServiceLifetime.Singleton);
    }

    /// <summary>
    /// Register the keyed <see cref="IIvrToolRegistry"/> for <paramref name="agentKey"/>.
    /// Invoked automatically by
    /// <see cref="AddIvrTool(IServiceCollection, string, string, Func{IServiceProvider, AIFunction}, ServiceLifetime)"/>;
    /// callers may invoke it directly to ensure an (initially empty) registry is resolvable for
    /// <paramref name="agentKey"/>.
    /// </summary>
    public static IServiceCollection AddIvrToolRegistry(this IServiceCollection services, string agentKey)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(agentKey);

        // Ensure the keyed builder exists even when no AddIvrTool was called yet, so
        // resolving the registry on an empty agent key yields an empty (rather than
        // throwing) registry. Calls to AddIvrTool further mutate the same instance.
        _ = FindOrAddBuilderDescriptor(services, agentKey);

        // Idempotent registration of the IIvrToolRegistry projection.
        if (!HasKeyedDescriptor(services, typeof(IIvrToolRegistry), agentKey))
        {
            services.AddKeyedSingleton<IIvrToolRegistry>(
                agentKey,
                (sp, key) =>
                {
                    var stringKey = key as string
                        ?? throw new InvalidOperationException(
                            $"{nameof(IIvrToolRegistry)} must be resolved with a string service key.");
                    return sp.GetRequiredKeyedService<IvrToolRegistryBuilder>(stringKey).Build();
                });
        }

        return services;
    }

    private static IvrToolRegistryBuilder FindOrAddBuilderDescriptor(IServiceCollection services, string agentKey)
    {
        // The builder is a singleton; we mutate it during DI setup so factories share the same
        // instance. Search the descriptor list for an existing keyed singleton implementation
        // instance; if absent, create one and register it.
        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == typeof(IvrToolRegistryBuilder)
                && descriptor.IsKeyedService
                && descriptor.ServiceKey is string existingKey
                && string.Equals(existingKey, agentKey, StringComparison.Ordinal)
                && descriptor.KeyedImplementationInstance is IvrToolRegistryBuilder instance)
            {
                return instance;
            }
        }

        var builder = new IvrToolRegistryBuilder(agentKey);
        services.AddKeyedSingleton(agentKey, builder);
        return builder;
    }

    private static bool HasKeyedDescriptor(IServiceCollection services, Type serviceType, string key)
    {
        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == serviceType
                && descriptor.IsKeyedService
                && descriptor.ServiceKey is string existing
                && string.Equals(existing, key, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
