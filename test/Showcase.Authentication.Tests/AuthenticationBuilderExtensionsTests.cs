using System.Text.Json;
using AI.TestingFramework.AspNetCore;
using Azure.Security.KeyVault.Keys;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Tokens;
using Showcase.Authentication.AspNetCore.ResourceServer.Authentication;
using Showcase.Authentication.AspNetCore.ResourceServer.Endpoints;
using Showcase.Authentication.AspNetCore.ResourceServer.KeySigning;
using Showcase.Authentication.Core;
using Showcase.Authentication.Tests.Helpers;
using Xunit;
using Xunit.v3;
namespace Showcase.Authentication.Tests;

public class AuthenticationBuilderExtensionsTests
{
    private static readonly Action<JwtBearerOptions> configureJwtOptions = (options) =>
    {
        options.Authority = TestAuthConstants.AuthorityWithTenantSpecified;
        options.Audience = TestAuthConstants.ApiClientId;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidAudience = TestAuthConstants.ApiClientId,
            ValidIssuer = TestAuthConstants.AadIssuer
        };

        options.MetadataAddress = TestAuthConstants.OpenIdMetadataAddress;
    };
   

    private static readonly Action<MicrosoftIdentityOptions> configureMsOptions = (options) =>
    {
        options.Instance = TestAuthConstants.AadInstance;
        options.TenantId = TestAuthConstants.TenantIdAsGuid;
        options.ClientId = TestAuthConstants.ClientId;
    };

    private static readonly ProtectedResourceMetadata metadata = new ()
    {
        Resource = new Uri("https://example.com", UriKind.Absolute),
        AuthorizationServers = new List<Uri>
        {
            new Uri("https://auth.example.com", UriKind.Absolute)
        },
        BearerMethodsSupported = new List<string> { "header", "body", "query" },
        ScopesSupported = new List<string> { "read", "write" },
        ResourceName = "Example Protected Resource",
        ResourceDocumentation = new Uri("https://example.com/docs", UriKind.Absolute),
        ResourcePolicyUri = new Uri("https://example.com/policy", UriKind.Absolute),
        ResourceTosUri = new Uri("https://example.com/tos", UriKind.Absolute),
    };

    private static readonly ProtectedResourceOptions minimalPrOptions = new ()
    {
    };

    private static readonly ProtectedResourceOptions fullPrOptions = new()
    {
        Metadata = metadata,
        ProtectedResourceMetadataAddress = new Uri("/.well-known/example-protected-resource", UriKind.Relative),
        RequireHttpsMetadata = true,
        EnableMetadataHealthCheck = false,
        JwksProvider = new KeyVaultJwksProviderOptions()
        {
            VaultUri = JwksProviderHelpers.KeyVaultUri,
            KeyName = JwksProviderHelpers.KeyVaultKeyName,
        }
    };
    public static TheoryData<string, ProtectedResourceOptions> AddProtectedResourcesParameters => new()
    {
        { JwtBearerDefaults.AuthenticationScheme, minimalPrOptions },
        { JwtBearerDefaults.AuthenticationScheme, fullPrOptions }
    };
    public static TheoryData<Uri, Uri> DiscoveryEndpoints => new()
    {
        { new Uri(ProtectedResourceConstants.DefaultOAuthProtectedResourcePathSuffix, UriKind.Relative), new Uri(ProtectedResourceConstants.JsonWebKeySetPathSuffix, UriKind.Relative) },
        { new Uri("https://example.com/.well-known/oauth-protected-resource", UriKind.Absolute), new Uri("https://example.com/.well-known/jwks", UriKind.Absolute) }
    };

    [Theory]
    [MemberData(nameof(AddProtectedResourcesParameters))]
    public async Task AddProtectedResource_ConfiguresOptionsWithMicrosoftIdentity(string authScheme, ProtectedResourceOptions prOptions)
    {
        // Arrange

        using var host = await CreateHost(authScheme, true, (options) => 
        {
            options.Metadata = prOptions.Metadata;
            options.ProtectedResourceMetadataAddress = prOptions.ProtectedResourceMetadataAddress;
            options.RequireHttpsMetadata = prOptions.RequireHttpsMetadata;
            options.EnableMetadataHealthCheck = prOptions.EnableMetadataHealthCheck;
            options.JwksProvider = prOptions.JwksProvider;

        }, (jwtOpt) => { }, configureMsOptions);
        var expectedOptions = prOptions;
        var expectedMetadata = expectedOptions.Metadata with { };

        if(expectedMetadata.AuthorizationServers?.Count <= 0)
        {
            expectedMetadata.AuthorizationServers.Add(new Uri(TestAuthConstants.AuthorityWithTenantSpecified));
        }

        var expectedMetadataString = JsonSerializer.Serialize(expectedMetadata, JsonContext.Default.ProtectedResourceMetadata);

        // Act
        var prOptionsMonitor = host.Services.GetRequiredService<IOptionsMonitor<ProtectedResourceOptions>>();
        var options = prOptionsMonitor.Get(authScheme);
        var metadata = options.Metadata;
        var metadataString = JsonSerializer.Serialize(metadata, JsonContext.Default.ProtectedResourceMetadata);

        // Assert
        Assert.NotNull(options);
        Assert.Equivalent(expectedOptions, options, true);
        Assert.Equivalent(expectedMetadata, metadata, true);
        Assert.Equivalent(expectedMetadata.AuthorizationServers, metadata.AuthorizationServers, true);
        Assert.Equal(expectedMetadataString, metadataString);
    }

    [Theory]
    [MemberData(nameof(AddProtectedResourcesParameters))]
    public async Task AddProtectedResource_ConfiguresOptionsWithJwtBearer(string authScheme, ProtectedResourceOptions prOptions)
    {
        // Arrange

        using var host = await CreateHost(authScheme, true, (options) =>
        {
            options.Metadata = prOptions.Metadata;
            options.ProtectedResourceMetadataAddress = prOptions.ProtectedResourceMetadataAddress;
            options.RequireHttpsMetadata = prOptions.RequireHttpsMetadata;
            options.EnableMetadataHealthCheck = prOptions.EnableMetadataHealthCheck;
            options.JwksProvider = prOptions.JwksProvider;

        }, configureJwtOptions, null);
        var expectedOptions = prOptions;
        var expectedMetadata = expectedOptions.Metadata with { };

        // The library applies some values if they are not already set
        if (expectedMetadata.AuthorizationServers?.Count <= 0)
        {
            expectedMetadata.AuthorizationServers.Add(new Uri(TestAuthConstants.AuthorityWithTenantSpecified));
        }

        var expectedMetadataString = JsonSerializer.Serialize(expectedMetadata, JsonContext.Default.ProtectedResourceMetadata);

        // Act
        var prOptionsMonitor = host.Services.GetRequiredService<IOptionsMonitor<ProtectedResourceOptions>>();
        var options = prOptionsMonitor.Get(authScheme);
        var metadata = options.Metadata;
        var metadataString = JsonSerializer.Serialize(metadata, JsonContext.Default.ProtectedResourceMetadata);

        // Assert
        Assert.NotNull(options);
        Assert.Equivalent(expectedOptions, options, true);
        Assert.Equivalent(expectedMetadata, metadata, true);
        Assert.Equivalent(expectedMetadata.AuthorizationServers, metadata.AuthorizationServers, true);
        Assert.Equal(expectedMetadataString, metadataString);
    }

    [Theory]
    [MemberData(nameof(DiscoveryEndpoints))]
    public async Task MapProtectedResourcesDiscovery_MapsMetadataDiscoveryEndpoint(Uri metadataAddress, Uri _)
    {
        // Arrange

        // Removing JwksUri from the expected metadata for this test
        var expectedMetadata = fullPrOptions.Metadata with { JwksUri = null };
        using var host = await CreateHost(JwtBearerDefaults.AuthenticationScheme, true, (options) =>
        {
            options.Metadata = expectedMetadata;
            options.ProtectedResourceMetadataAddress = metadataAddress;
            options.RequireHttpsMetadata = fullPrOptions.RequireHttpsMetadata;
            options.EnableMetadataHealthCheck = fullPrOptions.EnableMetadataHealthCheck;
            //options.JwksProvider = fullPrOptions.JwksProvider;

        }, configureJwtOptions, null);
        var localMetadataAddress = metadataAddress.IsAbsoluteUri ? metadataAddress.AbsolutePath : metadataAddress.ToString();
        //var localJwksAddress = jwksAddress.IsAbsoluteUri ? jwksAddress.AbsolutePath : jwksAddress.ToString();
        var expectedMetadataString = JsonSerializer.Serialize(expectedMetadata, JsonContext.Default.ProtectedResourceMetadata);

        // Act
        
        using var client = host.CreateClient();
        var response = await client.GetAsync(localMetadataAddress, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expectedMetadataString, responseBody);
    }

    [Theory]
    [MemberData(nameof(DiscoveryEndpoints))]
    public async Task MapProtectedResourcesDiscovery_MapsJwksDiscoveryEndpoint(Uri metadataAddress, Uri jwksAddress)
    {
        // Arrange

        // Removing JwksUri from the expected metadata for this test
        var expectedMetadata = fullPrOptions.Metadata with { JwksUri = jwksAddress };
        using var host = await CreateHost(JwtBearerDefaults.AuthenticationScheme, true, (options) =>
        {
            options.Metadata = expectedMetadata;
            options.ProtectedResourceMetadataAddress = metadataAddress;
            options.RequireHttpsMetadata = fullPrOptions.RequireHttpsMetadata;
            options.EnableMetadataHealthCheck = fullPrOptions.EnableMetadataHealthCheck;
            options.JwksProvider = fullPrOptions.JwksProvider;

        }, configureJwtOptions, null);
        var localJwksAddress = jwksAddress.IsAbsoluteUri ? jwksAddress.AbsolutePath : jwksAddress.ToString();

        // Act
        using var client = host.CreateClient();
        var jwksResponse = await client.GetAsync(localJwksAddress, TestContext.Current.CancellationToken);

        // Assert
        jwksResponse.EnsureSuccessStatusCode();
        var jwksBody = await jwksResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(jwksBody);
    }


    public static async Task<IHost> CreateHost(
        string authScheme,
        bool mapEndpoints,
        Action<ProtectedResourceOptions> configurePr,
        Action<JwtBearerOptions> configureJwtBearer,
        Action<MicrosoftIdentityOptions>? configureMsId = null)
    {
        var host = FakeHost.CreateBuilder((opt) => opt.ValidateOnBuild = false)
                .ConfigureWebHost(webhost =>
                {
                    
                    webhost.UseFakeStartup()
                    .ListenHttpsOnAnyPort()
                    .Configure(host =>
                    {
                        host.UseRouting();
                        host.UseAuthorization();
                        host.UseAuthentication();
                        if (mapEndpoints)
                        {
                            host.UseEndpoints(endpoints =>
                            {
                                endpoints.MapGet("/", () => "Hello");
                                endpoints.MapProtectedResourcesDiscovery(authScheme);
                            });
                        }

                    });
                })
            .ConfigureServices(services =>
                {
                    services.AddRouting();

                    var authBuilder = services.AddAuthentication(authScheme);
                    if (configureMsId is not null)
                    {
                        authBuilder.AddMicrosoftIdentityWebApi(configureJwtBearer, configureMsId, authScheme);
                    }
                    else
                    {
                        authBuilder.AddJwtBearer(authScheme, configureJwtBearer);
                    }
                    services.AddAuthProtectedResource(configurePr, authScheme);
                    services.AddAuthorization();

                    if (mapEndpoints)
                    {
                        services.RemoveAll<IAzureClientFactory<KeyClient>>();
                        services.AddSingleton<IAzureClientFactory<KeyClient>, AzureKeyClientFactoryMock>();
                    }

                })
            .Build();

        await host.StartAsync();
        return host;
    }
}
