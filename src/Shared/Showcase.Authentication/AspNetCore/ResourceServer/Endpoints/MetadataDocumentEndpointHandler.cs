using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Showcase.Authentication.AspNetCore.ResourceServer.Authentication;
using Showcase.Authentication.AspNetCore.ResourceServer.KeySigning;
using Showcase.Authentication.Core;

namespace Showcase.Authentication.AspNetCore.ResourceServer.Endpoints;

/// <summary>
/// Handles requests for the <see cref="ProtectedResourceMetadata">protected resource metadata</see> document.
/// </summary>
public class MetadataDocumentEndpointHandler : IDocumentEndpointHandler<ProtectedResourceMetadata>
{
    private readonly IOptionsMonitor<ProtectedResourceOptions> _optionsMonitor;
    private readonly string? _authenticationScheme;
    private readonly ILogger<MetadataDocumentEndpointHandler> _logger;

    public MetadataDocumentEndpointHandler(
        IOptionsMonitor<ProtectedResourceOptions> optionsMonitor,
        ILogger<MetadataDocumentEndpointHandler> logger,
        [ServiceKey] string? authenticationScheme = null)
    {
        ArgumentNullException.ThrowIfNull(optionsMonitor);
        _optionsMonitor = optionsMonitor;
        _authenticationScheme = authenticationScheme;
        _logger = logger;
    }

    public async Task<ProtectedResourceMetadata?> HandleAsync(HttpContext context)
    {
        var options = _optionsMonitor.GetKeyedOrCurrent(_authenticationScheme);

        if (options is null)
        {
            _logger.LogInformation("No ProtectedResourceOptions found for authentication scheme '{AuthenticationScheme}'.", _authenticationScheme);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return null;
        }

        // Copy metadata to avoid modifying the original options
        var metadata = options.Metadata with { };

        var baseUri = GetBaseRequestUri(context, options);

        metadata.Resource = metadata.Resource.IsAbsoluteUri
            ? metadata.Resource
            : baseUri;

        // Try to get the signed protected resource issuer from the service provider
        var protectedResourceIssuer = context.RequestServices.GeKeyedOrCurrentService<ISignedProtectedResourceIssuer>(_authenticationScheme, false);

        if (protectedResourceIssuer is not null)
        {
            metadata.JwksUri = metadata.JwksUri switch
            {
                { IsAbsoluteUri: true } => metadata.JwksUri, // If the path is absolute, use it directly
                { } => new Uri(baseUri, metadata.JwksUri), // Otherwise, combine with the base URI
                _ => new Uri(baseUri, ProtectedResourceConstants.JsonWebKeySetPathSuffix) // Otherwise, combine with the default JWKs path suffix
            };
            metadata.SignedMetadata = await protectedResourceIssuer.GetSignMetadataTokenAsync(options.Metadata, context.RequestAborted);
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(metadata, JsonContext.Default.ProtectedResourceMetadata.Options, context.RequestAborted).ConfigureAwait(false);

        return metadata;
    }

    private static Uri GetBaseRequestUri(HttpContext context, ProtectedResourceOptions options)
    {
        if (context.Request is not HttpRequest request) throw new InvalidOperationException("HttpContext.Request is null. Cannot determine absolute URI.");

        return new Uri($"{(options.RequireHttpsMetadata ? Uri.UriSchemeHttps : request.Scheme)}://{request.Host}{request.PathBase}");
    }
}
