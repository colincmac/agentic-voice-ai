using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Showcase.Authentication.AspNetCore.ResourceServer.KeySigning;
using Showcase.Authentication.Core;

namespace Showcase.Authentication.AspNetCore.ResourceServer.Endpoints;

/// <summary>
/// Invokes the <see cref="ISignedProtectedResourceIssuer"/> to retrieve the JSON Web Key Set (JWKS) document for the specified authentication scheme.
/// </summary>
public class JwksDocumentEndpointHandler : IDocumentEndpointHandler<JwksDocument>
{
    private readonly string? _authenticationScheme;
    private readonly ILogger<JwksDocumentEndpointHandler> _logger;

    public JwksDocumentEndpointHandler(
        ILogger<JwksDocumentEndpointHandler> logger,
        [ServiceKey] string? authenticationScheme = null)
    {
        _authenticationScheme = authenticationScheme;
        _logger = logger;
    }

    public async Task<JwksDocument?> HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var signingProvider = context.RequestServices.GeKeyedOrCurrentService<ISignedProtectedResourceIssuer>(_authenticationScheme, true);
        if (signingProvider is null)
        {
            _logger.LogError("No signing provider found for authentication scheme '{AuthenticationScheme}'.", _authenticationScheme);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return null;
        }

        var result = await signingProvider.GetJwksDocumentAsync(context.RequestAborted);
        context.Response.StatusCode = result is null ? StatusCodes.Status404NotFound : StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(result, JsonContext.Default.JwksDocument.Options, context.RequestAborted).ConfigureAwait(false);

        return result;
    }
}
