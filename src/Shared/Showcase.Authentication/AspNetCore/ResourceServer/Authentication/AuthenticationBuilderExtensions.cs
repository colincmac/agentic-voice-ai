using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Showcase.Authentication.AspNetCore.ResourceServer.Endpoints;
using Showcase.Authentication.AspNetCore.ResourceServer.KeySigning;
using Showcase.Authentication.AspNetCore.ResourceServer.Authorization;
using Showcase.Authentication.AspNetCore.ResourceServer.HealthChecks;
using Showcase.Authentication.Core;

namespace Showcase.Authentication.AspNetCore.ResourceServer.Authentication;
public static class AuthenticationBuilderExtensions
{

    /// <summary>
    ///
    /// </summary>
    /// <param name="builder">The Authentication Builder</param>
    /// <param name="configurationSection">The configuration section with the protected resource options</param>
    /// <param name="authenticationScheme">The authentication scheme to use for the defined protected resource</param>
    /// <param name="configureAllOptions">Optional delegate to configure the defined protected resource</param>
    /// <returns>The updated AuthenticationBuilder</returns>
    /// <remarks></remarks>
    public static IServiceCollection AddAuthProtectedResource(
        this IServiceCollection serviceCollection,
        IConfigurationSection configurationSection,
        string authenticationScheme = JwtBearerDefaults.AuthenticationScheme,
        Action<ProtectedResourceOptions>? configureOptions = null
        )
    {
        return serviceCollection.AddAuthProtectedResource(configurationSection.Bind, authenticationScheme); ;
    }

    /// <summary>
    /// Configures the authentication builder to add a protected resource to the specified authentication scheme. Values not provided in the options will be derived from the authentication scheme's configuration.
    /// </summary>
    /// <param name="builder">The Authentication Builder</param>
    /// <param name="configureOptions">Optional delegate to configure the defined protected resource</param>
    /// <param name="authenticationScheme">The authentication scheme to use for the defined protected resource</param>
    /// <returns>The updated AuthenticationBuilder</returns>
    public static IServiceCollection AddAuthProtectedResource(
        this IServiceCollection serviceCollection,
        Action<ProtectedResourceOptions> configureOptions,
        string authenticationScheme = JwtBearerDefaults.AuthenticationScheme
        )
    {
        // Configure the authentication scheme's events to include protected resource metadata in the WWW-Authenticate header
        serviceCollection.AddTransient<ProtectedResourceJwtBearerEvents>();
        serviceCollection.AddSingleton<ConfigureJwtBearerOptions>();
        
        serviceCollection.AddTransient<AuthorizationDetailsValidator>();

        serviceCollection.AddSingleton<IPostConfigureOptions<JwtBearerOptions>>(sp =>
        {
            var configure = sp.GetRequiredService<ConfigureJwtBearerOptions>();
            configure.Scheme = authenticationScheme;
            return configure;
        });

        serviceCollection.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, ConfigureJwtBearerOptions>();

        // Configure the options for the protected resource. This is either the host itself or a hosted resource on the server
        // e.g. `https://example.com` or `https://example.com/hostedResourceName`.
        // We're not adding additional schemes, just extending an existing one to include protected resource metadata.

        // Can't reference IOptionsMonitor<JwtBearerOptions> directly in PostConfigure for some reason
        serviceCollection.AddOptions<ProtectedResourceOptions>(authenticationScheme)
            .Configure(configureOptions)
            .PostConfigure<IServiceProvider>((protectedResourceOptions, serviceProvider) =>
            {
                var jwtOptionsMonitor = serviceProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();
                var jwtOptions =  jwtOptionsMonitor.GetKeyedOrCurrent(authenticationScheme);

                // Add the scheme's authorization servers if not already set
                if (protectedResourceOptions.Metadata.AuthorizationServers?.Count == 0 && !string.IsNullOrEmpty(jwtOptions.Authority))
                {
                    protectedResourceOptions.Metadata.AuthorizationServers.Add(new Uri(jwtOptions.Authority));
                }

                // Add the scheme's scopes if not already set
                if (protectedResourceOptions.Metadata.ScopesSupported?.Count == 0 && jwtOptions.Configuration?.ScopesSupported != null)
                {
                    protectedResourceOptions.Metadata.ScopesSupported.AddRange(jwtOptions.Configuration.ScopesSupported);
                }

            });

        serviceCollection.AddMonitoredDiscoveryEndpoint<ProtectedResourceMetadata, MetadataDocumentEndpointHandler>(authenticationScheme);
        serviceCollection.AddMonitoredDiscoveryEndpoint<JwksDocument, JwksDocumentEndpointHandler>(authenticationScheme);
        serviceCollection.ConfigureKnownMetadataSigningServices(authenticationScheme);
        return serviceCollection;
    }


    #region (Optional) Signed Protected Metadata Section
    private static IServiceCollection ConfigureKnownMetadataSigningServices(this IServiceCollection services, string authenticationScheme)
    {
        var options = services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<ProtectedResourceOptions>>().GetKeyedOrCurrent(authenticationScheme);
        return options.JwksProvider switch
        {
            KeyVaultJwksProviderOptions keyVaultOptions => services.AddAzureKeyVaultSigning(keyVaultOptions, authenticationScheme),
            _ => services
        };
    }

    private static IServiceCollection AddAzureKeyVaultSigning(this IServiceCollection services, KeyVaultJwksProviderOptions options, string authenticationScheme)
    {
        if (string.IsNullOrEmpty(options.VaultUri))
            throw new ArgumentException("SigningKeyVaultUri must be set, with either a Signing Key or Certificate name, must not be null  must be set for Azure Key Vault signing key.");

        var vaultUri = new Uri(options.VaultUri);

        services.AddAzureClients(builder =>
        {
            builder.AddKeyClient(vaultUri).WithName(authenticationScheme);
            builder.AddCertificateClient(vaultUri).WithName(authenticationScheme);
            builder.AddCryptographyClient(vaultUri).WithName(authenticationScheme);
            builder.UseCredential(options.AzureTokenCredential);
        });

        services.AddKeyedSingleton<ISignedProtectedResourceIssuer, AzureKeyVaultProtectedResourceIssuer>(authenticationScheme);
        services.AddMonitoredDiscoveryEndpoint<JwksDocument, JwksDocumentEndpointHandler>(authenticationScheme);
        return services;
    }
    #endregion

    /// <summary>
    /// </summary>
    /// <param name="authenticationBuilder">The authentication builder.</param>
    /// <param name="configureOptions">Optional configuration for DPoP options.</param>
    /// <returns>The authentication builder for chaining.</returns>
    public static AuthenticationBuilder AddDPoP(
        this AuthenticationBuilder authenticationBuilder,
        Action<DPoPAuthenticationOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(authenticationBuilder);

        authenticationBuilder.AddScheme<DPoPAuthenticationOptions, DPoPAuthenticationHandler>(
            ProtectedResourceConstants.AuthenticationSchemes.DPoP,
            ProtectedResourceConstants.AuthenticationSchemes.DPoP,
            configureOptions);

        return authenticationBuilder;
    }

    /// <summary>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="authenticationScheme">The authentication scheme to monitor (defaults to Bearer).</param>
    /// <returns>The health checks builder for further configuration.</returns>
    public static IHealthChecksBuilder AddProtectedResourceHealthChecks(
        this IServiceCollection services,
        string authenticationScheme = JwtBearerDefaults.AuthenticationScheme)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddHealthChecks()
            .AddProtectedResourceMetadata(authenticationScheme);
    }

    /// <summary>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="authenticationSchemes">The authentication schemes to monitor.</param>
    /// <returns>The health checks builder for further configuration.</returns>
    public static IHealthChecksBuilder AddProtectedResourceHealthChecksForSchemes(
        this IServiceCollection services,
        params string[] authenticationSchemes)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(authenticationSchemes);

        return services.AddHealthChecks()
            .AddProtectedResourceMetadataForSchemes(authenticationSchemes);
    }

    /// <summary>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAuthorizationDetailsValidation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddTransient<AuthorizationDetailsValidator>();
        return services;
    }

    private static IServiceCollection AddMonitoredDiscoveryEndpoint<TDocumentType, TProvider>(this IServiceCollection services, string authenticationScheme)
        where TDocumentType : class
        where TProvider : class, IDocumentEndpointHandler<TDocumentType>
    {
        services.AddKeyedScoped<IDocumentEndpointHandler<TDocumentType>, TProvider>(authenticationScheme);

        // For health checks discovery.
        //services.AddSingleton(new NamedService<IDocumentEndpointHandler<TDocumentType>>(authenticationScheme));
        return services;
    }

}
