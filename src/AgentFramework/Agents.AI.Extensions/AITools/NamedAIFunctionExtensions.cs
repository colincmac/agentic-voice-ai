using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agents.AI.Extensions.AITools;

/// <summary>
/// DI extensions that register named <see cref="AIFunction"/> services and the
/// <see cref="INamedAIFunctionProvider"/> that resolves them.
/// </summary>
public static class NamedAIFunctionExtensions
{
    /// <summary>
    /// Register the default <see cref="INamedAIFunctionProvider"/>. Called automatically
    /// by <see cref="AddNamedAIFunction(IServiceCollection, string, Func{IServiceProvider, AIFunction}, ServiceLifetime)"/>;
    /// call directly when only <see cref="IAIToolCollection"/> implementations supply tools.
    /// </summary>
    public static IServiceCollection AddNamedAIFunctionProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<INamedAIFunctionProvider, NamedAIFunctionProvider>();
        return services;
    }

    /// <summary>
    /// Register an <see cref="AIFunction"/> under <paramref name="name"/> with the
    /// given <paramref name="lifetime"/>. Re-registering the same name with a
    /// different factory overwrites the previous entry (last-wins), mirroring the
    /// behavior of keyed DI.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The lookup name. Should match what workflow definitions reference.</param>
    /// <param name="factory">Factory that produces the function from the resolving scope. Must return non-null.</param>
    /// <param name="lifetime">
    /// <see cref="ServiceLifetime.Singleton"/> for process-wide stateless tools,
    /// <see cref="ServiceLifetime.Scoped"/> for per-call tools that need scoped state
    /// (e.g. <c>CallerAuthenticationState</c>, <c>ICallSessionAccessor</c>),
    /// <see cref="ServiceLifetime.Transient"/> when each invocation should produce a fresh instance.
    /// </param>
    public static IServiceCollection AddNamedAIFunction(
        this IServiceCollection services,
        string name,
        Func<IServiceProvider, AIFunction> factory,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(factory);

        services.AddNamedAIFunctionProvider();

        // Last-wins semantics: an existing registration under the same name is replaced.
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == typeof(AIFunction)
                && descriptor.IsKeyedService
                && descriptor.ServiceKey is string existing
                && string.Equals(existing, name, StringComparison.Ordinal))
            {
                services.RemoveAt(i);
            }
        }

        services.Add(new ServiceDescriptor(
            typeof(AIFunction),
            name,
            (sp, _) => factory(sp) ?? throw new InvalidOperationException(
                $"Factory for named AIFunction '{name}' returned null."),
            lifetime));

        return services;
    }

    /// <summary>
    /// Convenience overload that registers a <em>singleton</em> <see cref="AIFunction"/>
    /// instance under <paramref name="name"/>. Equivalent to calling
    /// <see cref="AddNamedAIFunction(IServiceCollection, string, Func{IServiceProvider, AIFunction}, ServiceLifetime)"/>
    /// with a factory that returns <paramref name="function"/>.
    /// </summary>
    public static IServiceCollection AddNamedAIFunction(
        this IServiceCollection services,
        string name,
        AIFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return services.AddNamedAIFunction(name, _ => function, ServiceLifetime.Singleton);
    }
}
