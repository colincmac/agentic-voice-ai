using Agents.AI.ContactCenter.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.DependencyInjection;

/// <summary>
/// Extension methods for registering Azure Speech services with dependency injection.
/// </summary>
public static class AzureSpeechServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Azure Speech Service with the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration section containing Azure Speech options (defaults to "AzureSpeech").</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAzureSpeech(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        return services.AddAzureSpeech(configuration.GetSection(AzureSpeechServiceOptions.SectionName));
    }

    /// <summary>
    /// Registers the Azure Speech Service with the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configurationSection">Configuration section containing Azure Speech options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAzureSpeech(
        this IServiceCollection services,
        IConfigurationSection configurationSection)
    {
        services.AddOptions<AzureSpeechServiceOptions>()
            .Bind(configurationSection)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<AzureSpeechService>();

        return services;
    }

    /// <summary>
    /// Registers the Azure Speech Service with the service collection using a configuration delegate.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">A delegate to configure Azure Speech options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAzureSpeech(
        this IServiceCollection services,
        Action<AzureSpeechServiceOptions> configure)
    {
        services.AddOptions<AzureSpeechServiceOptions>()
            .Configure(configure)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<AzureSpeechService>();

        return services;
    }

    /// <summary>
    /// Registers the Azure Speech Service with explicit options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The Azure Speech options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAzureSpeech(
        this IServiceCollection services,
        AzureSpeechServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(sp =>
        {
            var logger = sp.GetService<ILogger<AzureSpeechService>>();
            return new AzureSpeechService(options, logger);
        });

        return services;
    }
}
