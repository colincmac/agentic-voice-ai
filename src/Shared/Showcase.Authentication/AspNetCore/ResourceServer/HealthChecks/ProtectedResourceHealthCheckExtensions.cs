using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Showcase.Authentication.AspNetCore.ResourceServer.Authentication;

namespace Showcase.Authentication.AspNetCore.ResourceServer.HealthChecks;

/// <summary>
/// </summary>
public static class ProtectedResourceHealthCheckExtensions
{
    /// <summary>
    /// </summary>
    /// <param name="authenticationScheme">The authentication scheme to check (defaults to "Bearer").</param>
    public static IHealthChecksBuilder AddProtectedResourceMetadata(
        this IHealthChecksBuilder builder,
        string authenticationScheme = "Bearer",
        string? name = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        name ??= $"protected_resource_metadata_{authenticationScheme}";
        
        return builder.Add(new HealthCheckRegistration(
            name,
            serviceProvider =>
            {
                var optionsMonitor = serviceProvider.GetRequiredService<IOptionsMonitor<ProtectedResourceOptions>>();
                var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
                var logger = serviceProvider.GetRequiredService<ILogger<ProtectedResourceMetadataHealthCheck>>();
                
                var httpClient = httpClientFactory.CreateClient(name);
                httpClient.Timeout = timeout ?? TimeSpan.FromSeconds(30);
                
                return new ProtectedResourceMetadataHealthCheck(optionsMonitor, httpClient, logger, authenticationScheme);
            },
            failureStatus,
            tags,
            timeout));
    }

    /// <summary>
    /// </summary>
    /// <param name="authenticationSchemes">The authentication schemes to check.</param>
    public static IHealthChecksBuilder AddProtectedResourceMetadataForSchemes(
        this IHealthChecksBuilder builder,
        IEnumerable<string> authenticationSchemes,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(authenticationSchemes);

        foreach (var scheme in authenticationSchemes)
        {
            builder.AddProtectedResourceMetadata(
                authenticationScheme: scheme,
                failureStatus: failureStatus,
                tags: tags,
                timeout: timeout);
        }

        return builder;
    }
}
