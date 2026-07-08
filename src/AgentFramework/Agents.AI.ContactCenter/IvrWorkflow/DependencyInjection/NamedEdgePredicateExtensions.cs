using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agents.AI.ContactCenter.IvrWorkflow.Predicates;

/// <summary>DI extensions for registering named <see cref="EdgePredicate"/> instances.</summary>
public static class NamedEdgePredicateExtensions
{
    /// <summary>
    /// Register the default <see cref="INamedEdgePredicateProvider"/>. Called automatically
    /// by <see cref="AddNamedEdgePredicate(IServiceCollection, string, Func{IServiceProvider, EdgePredicate}, ServiceLifetime)"/>;
    /// call directly when predicates are added by other means.
    /// </summary>
    public static IServiceCollection AddNamedEdgePredicateProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<INamedEdgePredicateProvider, NamedEdgePredicateProvider>();
        return services;
    }

    /// <summary>Register a named <see cref="EdgePredicate"/>. Last-wins on duplicate names.</summary>
    public static IServiceCollection AddNamedEdgePredicate(
        this IServiceCollection services,
        string name,
        Func<IServiceProvider, EdgePredicate> factory,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(factory);

        services.AddNamedEdgePredicateProvider();

        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == typeof(EdgePredicate)
                && descriptor.IsKeyedService
                && descriptor.ServiceKey is string existing
                && string.Equals(existing, name, StringComparison.Ordinal))
            {
                services.RemoveAt(i);
            }
        }

        services.Add(new ServiceDescriptor(
            typeof(EdgePredicate),
            name,
            (sp, _) => factory(sp) ?? throw new InvalidOperationException(
                $"Factory for named EdgePredicate '{name}' returned null."),
            lifetime));

        return services;
    }

    /// <summary>Singleton-instance overload of <see cref="AddNamedEdgePredicate(IServiceCollection, string, Func{IServiceProvider, EdgePredicate}, ServiceLifetime)"/>.</summary>
    public static IServiceCollection AddNamedEdgePredicate(
        this IServiceCollection services,
        string name,
        EdgePredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return services.AddNamedEdgePredicate(name, _ => predicate, ServiceLifetime.Singleton);
    }
}
