using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Showcase.Authentication.Core;
using System.Text;


namespace Showcase.Authentication.AspNetCore.ResourceServer.Authentication;
public sealed class ProtectedResourceJwtBearerEvents
{
    private readonly IOptionsMonitor<ProtectedResourceOptions> _optionsMonitor;
    private readonly ILogger<ProtectedResourceJwtBearerEvents> _logger;

    public ProtectedResourceJwtBearerEvents(
        IOptionsMonitor<ProtectedResourceOptions> protectedResourceOptionsMonitor,
        ILogger<ProtectedResourceJwtBearerEvents> logger
        )
    {
        _optionsMonitor = protectedResourceOptionsMonitor;
        _logger = logger;
    }


    /// <summary>
    /// Add's the protected resource metadata URI to the WWW-Authenticate header in the challenge response.
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    /// <remarks>
    /// While this can be invoked in isolation to add the resource_metadata, it is typically used in conjunction with the <see cref="JwtBearerEvents.Challenge"/> event.
    /// </remarks>
    public Task Challenge(JwtBearerChallengeContext context)
    {
        var options = _optionsMonitor.GetKeyedOrCurrent(context.Scheme.Name);

        if (options == null)
        {
            _logger.LogDebug("No ProtectedResourceOptions found for scheme: {Scheme}. Skipping challenge modification.", context.Scheme.Name);
            return Task.CompletedTask;
        }

        ValidateProtectedResourceOptions(options, context.Options.RequireHttpsMetadata);
        
        // This URI needs to match "<resource-host>/<default-resource-discovery-endpoint>/<optional-hosted-resource>".
        var resourceMetadataUri = options.ProtectedResourceMetadataAddress switch
        {
            { IsAbsoluteUri: true } => options.ProtectedResourceMetadataAddress, // If the path is absolute, use it directly
            _ => new Uri(GetBaseRequestUri(context.Request, context.Options.RequireHttpsMetadata), options.ProtectedResourceMetadataAddress)
        };


        context.Response.StatusCode = StatusCodes.Status401Unauthorized;

        // WWW-Authenticate: Bearer resource_metadata="https://example.com/.well-known/oauth-resource-metadata/<optional-hosted-resource>"
        var stringBuilder = new StringBuilder();
        // Add the scheme's challenge to the WWW-Authenticate header if not already present
        if (!context.Response.Headers.WWWAuthenticate.Contains(context.Options.Challenge))
        {
            stringBuilder.Append(context.Options.Challenge);
        }

        if (context.Options.Challenge.IndexOf(' ') > 0)
        {
            // Only add a comma after the first param, if any
            stringBuilder.Append(',');
        }

        stringBuilder.Append(" resource_metadata=\"");
        stringBuilder.Append(resourceMetadataUri);
        stringBuilder.Append('\"');

        _logger.LogDebug("Adding Protected Metadata to Challenge header {Header} for scheme: {Scheme}", stringBuilder.ToString(), context.Scheme.Name);

        context.Response.Headers.Append(HeaderNames.WWWAuthenticate, stringBuilder.ToString());

        return Task.CompletedTask;
    }
    private static Uri GetBaseRequestUri(HttpRequest request, bool requireHttps) => new($"{(requireHttps ? Uri.UriSchemeHttps : request.Scheme)}://{request.Host}{request.PathBase}");

    /// <summary>
    /// </summary>
    /// <param name="options">The protected resource options to validate.</param>
    /// <param name="requireHttpsMetadata">Whether HTTPS is required for metadata.</param>
    private static void ValidateProtectedResourceOptions(ProtectedResourceOptions options, bool requireHttpsMetadata)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Metadata);

        if (options.Metadata.Resource == null)
        {
            throw new InvalidOperationException("Protected resource metadata must have a valid resource URI.");
        }

        if (requireHttpsMetadata && options.ProtectedResourceMetadataAddress.IsAbsoluteUri && 
            !string.Equals(options.ProtectedResourceMetadataAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Protected resource metadata address must use HTTPS when RequireHttpsMetadata is true. Current scheme: {options.ProtectedResourceMetadataAddress.Scheme}");
        }

        if (options.Metadata.AuthorizationServers?.Count > 0)
        {
            foreach (var authServer in options.Metadata.AuthorizationServers)
            {
                if (authServer == null)
                {
                    throw new InvalidOperationException("Authorization server URI cannot be null.");
                }

                if (requireHttpsMetadata && !string.Equals(authServer.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Authorization server URI must use HTTPS when RequireHttpsMetadata is true. URI: {authServer}");
                }
            }
        }

        if (options.Metadata.JwksUri != null)
        {
            if (requireHttpsMetadata && !string.Equals(options.Metadata.JwksUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"JWKS URI must use HTTPS when RequireHttpsMetadata is true. URI: {options.Metadata.JwksUri}");
            }
        }

        ValidateOptionalUri(options.Metadata.ResourceDocumentation, nameof(options.Metadata.ResourceDocumentation), requireHttpsMetadata);
        ValidateOptionalUri(options.Metadata.ResourcePolicyUri, nameof(options.Metadata.ResourcePolicyUri), requireHttpsMetadata);
        ValidateOptionalUri(options.Metadata.ResourceTosUri, nameof(options.Metadata.ResourceTosUri), requireHttpsMetadata);

        if (options.Metadata.BearerMethodsSupported?.Count > 0)
        {
            var validMethods = new[] { "header", "body", "query" };
            var invalidMethods = options.Metadata.BearerMethodsSupported.Except(validMethods).ToList();
            if (invalidMethods.Count != 0)
            {
                throw new InvalidOperationException($"Invalid bearer methods: {string.Join(", ", invalidMethods)}. Valid methods are: {string.Join(", ", validMethods)}");
            }
        }

        if (options.Metadata.DpopSigningAlgValuesSupported?.Count > 0)
        {
            var validAlgorithms = new[] { "RS256", "RS384", "RS512", "ES256", "ES384", "ES512", "PS256", "PS384", "PS512" };
            var invalidAlgorithms = options.Metadata.DpopSigningAlgValuesSupported.Except(validAlgorithms).ToList();
            if (invalidAlgorithms.Count != 0)
            {
                throw new InvalidOperationException($"Invalid DPoP signing algorithms: {string.Join(", ", invalidAlgorithms)}. Valid algorithms are: {string.Join(", ", validAlgorithms)}");
            }
        }

        if (options.Metadata.ResourceSigningAlgValuesSupported?.Count > 0)
        {
            var validAlgorithms = new[] { "RS256", "RS384", "RS512", "ES256", "ES384", "ES512", "PS256", "PS384", "PS512" };
            var invalidAlgorithms = options.Metadata.ResourceSigningAlgValuesSupported.Except(validAlgorithms).ToList();
            if (invalidAlgorithms.Count != 0)
            {
                throw new InvalidOperationException($"Invalid resource signing algorithms: {string.Join(", ", invalidAlgorithms)}. Valid algorithms are: {string.Join(", ", validAlgorithms)}");
            }
        }
    }

    private static void ValidateOptionalUri(Uri? uri, string propertyName, bool requireHttps)
    {
        if (uri != null && requireHttps && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{propertyName} must use HTTPS when RequireHttpsMetadata is true. URI: {uri}");
        }
    }

}
