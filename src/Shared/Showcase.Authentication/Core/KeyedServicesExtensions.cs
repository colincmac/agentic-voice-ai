using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace Showcase.Authentication.Core;

public static class KeyedServicesExtensions
{

    /// <summary>
    /// Gets the options for the given service key, or the current value if the service key is <see langword="null"/>.
    /// </summary>
    /// <typeparam name="TOptions">The options type.</typeparam>
    /// <param name="optionsMonitor">The <see cref="IOptionsMonitor{TOptions}"/> to load the options object from.</param>
    /// <param name="serviceKey">An optional string that specifies the name of the options object to get.</param>
    /// <returns>The <typeparamref name="TOptions"/> instance.</returns>
    public static TOptions GetKeyedOrCurrent<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        this IOptionsMonitor<TOptions> optionsMonitor, string? serviceKey)
        where TOptions : class
    {
        if (serviceKey is null)
        {
            return optionsMonitor.CurrentValue;
        }

        return optionsMonitor.Get(serviceKey);
    }

    /// <summary>
    /// Gets a keyed service from the <see cref="IServiceProvider"/>, or a non-keyed service if the key is <see langword="null"/>.
    /// </summary>
    /// <typeparam name="T">The type of service object to get.</typeparam>
    /// <param name="provider">The <see cref="IServiceProvider"/> to retrieve the service object from.</param>
    /// <param name="serviceKey">An optional string that specifies the key of service object to get.</param>
    /// <returns>A service object of type <typeparamref name="T"/>.</returns>
    /// <exception cref="InvalidOperationException">There is no service of type <typeparamref name="T"/> registered.</exception>
    public static TService? GeKeyedOrCurrentService<TService>(this IServiceProvider provider, string? serviceKey = null, bool? required = true)
        where TService : notnull
    {
        return (serviceKey, required) switch
        {
            (null, true) => provider.GetRequiredService<TService>(),
            (null, false) => provider.GetService<TService>(),
            (not null, true) => provider.GetRequiredKeyedService<TService>(serviceKey),
            (not null, false) => provider.GetKeyedService<TService>(serviceKey),
            _ => default,
        };
    }

    public static TService GeKeyedOrCurrentRequiredService<TService>(this IServiceProvider provider, string? serviceKey = null)
        where TService : notnull
    {
        return serviceKey switch
        {
            null => provider.GetRequiredService<TService>(),
            _ => provider.GetRequiredKeyedService<TService>(serviceKey),
        };
    }
}
