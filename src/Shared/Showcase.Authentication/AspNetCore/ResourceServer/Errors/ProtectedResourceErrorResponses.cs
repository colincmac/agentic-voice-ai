using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using System.Text.Json;

namespace Showcase.Authentication.AspNetCore.ResourceServer.Errors;

/// <summary>
/// </summary>
public static class ProtectedResourceErrorResponses
{
    /// <summary>
    /// </summary>
    public static async Task WriteInvalidTokenError(HttpContext context, string? description = null, string? resourceMetadataUri = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";

        var wwwAuthenticateHeader = BuildWwwAuthenticateHeader("invalid_token", description, resourceMetadataUri);
        context.Response.Headers.Append(HeaderNames.WWWAuthenticate, wwwAuthenticateHeader);

        var errorResponse = new
        {
            error = "invalid_token",
            error_description = description ?? "The access token provided is expired, revoked, malformed, or invalid for other reasons."
        };

        var json = JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true
        });

        await context.Response.WriteAsync(json);
    }

    /// <summary>
    /// </summary>
    public static async Task WriteInsufficientScopeError(HttpContext context, string scope, string? resourceMetadataUri = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scope);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";

        var description = $"The request requires higher privileges than provided by the access token. Required scope: {scope}";
        var wwwAuthenticateHeader = BuildWwwAuthenticateHeader("insufficient_scope", description, resourceMetadataUri, scope);
        context.Response.Headers.Append(HeaderNames.WWWAuthenticate, wwwAuthenticateHeader);

        var errorResponse = new
        {
            error = "insufficient_scope",
            error_description = description,
            scope = scope
        };

        var json = JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true
        });

        await context.Response.WriteAsync(json);
    }

    /// <summary>
    /// </summary>
    public static async Task WriteDPoPRequiredError(HttpContext context, string? resourceMetadataUri = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";

        var description = "DPoP proof JWT is required for accessing this resource.";
        var wwwAuthenticateHeader = BuildDPoPWwwAuthenticateHeader(description, resourceMetadataUri);
        context.Response.Headers.Append(HeaderNames.WWWAuthenticate, wwwAuthenticateHeader);

        var errorResponse = new
        {
            error = "use_dpop_nonce",
            error_description = description
        };

        var json = JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true
        });

        await context.Response.WriteAsync(json);
    }

    /// <summary>
    /// </summary>
    public static async Task WriteInvalidDPoPProofError(HttpContext context, string? description = null, string? resourceMetadataUri = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json";

        var errorDescription = description ?? "The DPoP proof JWT is invalid, malformed, or does not match the request.";
        var wwwAuthenticateHeader = BuildDPoPWwwAuthenticateHeader(errorDescription, resourceMetadataUri);
        context.Response.Headers.Append(HeaderNames.WWWAuthenticate, wwwAuthenticateHeader);

        var errorResponse = new
        {
            error = "invalid_dpop_proof",
            error_description = errorDescription
        };

        var json = JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true
        });

        await context.Response.WriteAsync(json);
    }

    /// <summary>
    /// </summary>
    public static async Task WriteAuthorizationDetailsError(HttpContext context, string description, string? resourceMetadataUri = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(description);

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json";

        var wwwAuthenticateHeader = BuildWwwAuthenticateHeader("invalid_authorization_details", description, resourceMetadataUri);
        context.Response.Headers.Append(HeaderNames.WWWAuthenticate, wwwAuthenticateHeader);

        var errorResponse = new
        {
            error = "invalid_authorization_details",
            error_description = description
        };

        var json = JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true
        });

        await context.Response.WriteAsync(json);
    }

    /// <summary>
    /// </summary>
    public static async Task WriteProtectedResourceError(HttpContext context, string error, string description, int statusCode = StatusCodes.Status400BadRequest, string? resourceMetadataUri = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(description);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var wwwAuthenticateHeader = BuildWwwAuthenticateHeader(error, description, resourceMetadataUri);
        context.Response.Headers.Append(HeaderNames.WWWAuthenticate, wwwAuthenticateHeader);

        var errorResponse = new
        {
            error = error,
            error_description = description
        };

        var json = JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true
        });

        await context.Response.WriteAsync(json);
    }

    /// <summary>
    /// </summary>
    private static string BuildWwwAuthenticateHeader(string error, string? description = null, string? resourceMetadataUri = null, string? scope = null)
    {
        var headerParts = new List<string> { "Bearer" };

        headerParts.Add($"error=\"{error}\"");

        if (!string.IsNullOrEmpty(description))
        {
            headerParts.Add($"error_description=\"{EscapeHeaderValue(description)}\"");
        }

        if (!string.IsNullOrEmpty(resourceMetadataUri))
        {
            headerParts.Add($"resource_metadata=\"{resourceMetadataUri}\"");
        }

        if (!string.IsNullOrEmpty(scope))
        {
            headerParts.Add($"scope=\"{scope}\"");
        }

        return string.Join(" ", headerParts);
    }

    /// <summary>
    /// </summary>
    private static string BuildDPoPWwwAuthenticateHeader(string? description = null, string? resourceMetadataUri = null)
    {
        var headerParts = new List<string> { "DPoP" };

        if (!string.IsNullOrEmpty(description))
        {
            headerParts.Add($"error_description=\"{EscapeHeaderValue(description)}\"");
        }

        if (!string.IsNullOrEmpty(resourceMetadataUri))
        {
            headerParts.Add($"resource_metadata=\"{resourceMetadataUri}\"");
        }

        return string.Join(" ", headerParts);
    }

    /// <summary>
    /// </summary>
    private static string EscapeHeaderValue(string value)
    {
        return value.Replace("\"", "\\\"").Replace("\\", "\\\\");
    }
}
