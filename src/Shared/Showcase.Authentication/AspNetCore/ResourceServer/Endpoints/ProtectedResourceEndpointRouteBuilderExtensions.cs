using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Showcase.Authentication.AspNetCore.ResourceServer.Authentication;
using Showcase.Authentication.Core;

namespace Showcase.Authentication.AspNetCore.ResourceServer.Endpoints;
public static class ProtectedResourceEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the resource metadata endpoint for OAuth authorization based on RFC 9728.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>An endpoint convention builder for further configuration.</returns>
    public static IEndpointRouteBuilder MapProtectedResourcesDiscovery(
        this IEndpointRouteBuilder endpoints,
        string? authenticationScheme = JwtBearerDefaults.AuthenticationScheme)
    {

        var optionsMonitor = endpoints.ServiceProvider.GetRequiredService<IOptionsMonitor<ProtectedResourceOptions>>();

        var currentOptions = optionsMonitor?.GetKeyedOrCurrent(authenticationScheme) ?? throw new InvalidOperationException($"No ProtectedResourceOptions found for authentication scheme '{authenticationScheme}'.");

        var friendlyName = currentOptions.Metadata.ResourceName ?? currentOptions.Metadata.Resource.ToString();

        var metadataPath = currentOptions.ProtectedResourceMetadataAddress.IsAbsoluteUri
            ? currentOptions.ProtectedResourceMetadataAddress.AbsolutePath
            : currentOptions.ProtectedResourceMetadataAddress.ToString();

        endpoints.MapGet(metadataPath, async (HttpContext context) =>
        {
            var provider = context.RequestServices.GetRequiredKeyedService<IDocumentEndpointHandler<ProtectedResourceMetadata>>(authenticationScheme);
            await provider.HandleAsync(context);
        })
            .WithDisplayName($"Protected Resource Metadata: {friendlyName}")
            .AllowAnonymous();

        if (currentOptions.JwksProvider != null && currentOptions.Metadata.JwksUri is Uri jwksUri)
        {

            var jwksPath = jwksUri.IsAbsoluteUri ? currentOptions.Metadata.JwksUri.AbsolutePath : jwksUri.ToString();

            if (string.IsNullOrEmpty(jwksPath))
            {
                throw new InvalidOperationException($"The JWKS URI path is empty for authentication scheme '{authenticationScheme}'.");
            }

            endpoints.MapGet(jwksPath, async (HttpContext context) =>
            {
                var provider = context.RequestServices.GetRequiredKeyedService<IDocumentEndpointHandler<JwksDocument>>(authenticationScheme);
                await provider.HandleAsync(context);
            })
                .WithDisplayName($"Protected Resource Metadata Signing Keys: {friendlyName}")
                .AllowAnonymous();
        }

        return endpoints;
    }
}
