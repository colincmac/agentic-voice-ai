using Agents.AI.ContactCenter.Azure;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Media.Audio;
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
    /// Registers the Azure Speech Service with the service collection as both
    /// <see cref="ISpeechRecognizer"/> and <see cref="ISpeechSynthesizer"/>.
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
    /// Registers the Azure Speech Service with the service collection as both
    /// <see cref="ISpeechRecognizer"/> and <see cref="ISpeechSynthesizer"/>.
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

        RegisterService(services);

        return services;
    }

    /// <summary>
    /// Registers the Azure Speech Service with the service collection using a configuration delegate.
    /// Service is registered as both <see cref="ISpeechRecognizer"/> and <see cref="ISpeechSynthesizer"/>.
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

        RegisterService(services);

        return services;
    }

    /// <summary>
    /// Registers the Azure Speech Service with explicit options.
    /// Service is registered as both <see cref="ISpeechRecognizer"/> and <see cref="ISpeechSynthesizer"/>.
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

        // Register as both interfaces, forwarding to the concrete service
        services.TryAddSingleton<ISpeechRecognizer>(sp => sp.GetRequiredService<AzureSpeechService>());
        services.TryAddSingleton<ISpeechSynthesizer>(sp => sp.GetRequiredService<AzureSpeechService>());

        return services;
    }

    private static void RegisterService(IServiceCollection services)
    {
        // Register the concrete service
        services.TryAddSingleton<AzureSpeechService>();

        // Register as both interfaces, forwarding to the concrete service
        services.TryAddSingleton<ISpeechRecognizer>(sp => sp.GetRequiredService<AzureSpeechService>());
        services.TryAddSingleton<ISpeechSynthesizer>(sp => sp.GetRequiredService<AzureSpeechService>());
    }
}
