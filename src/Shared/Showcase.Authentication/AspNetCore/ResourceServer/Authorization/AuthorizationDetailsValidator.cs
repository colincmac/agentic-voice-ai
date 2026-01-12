using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Showcase.Authentication.AspNetCore.ResourceServer.Authentication;
using System.Text.Json;

namespace Showcase.Authentication.AspNetCore.ResourceServer.Authorization;

/// <summary>
/// </summary>
public class AuthorizationDetailsValidator
{
    private readonly IOptionsMonitor<ProtectedResourceOptions> _optionsMonitor;
    private readonly ILogger<AuthorizationDetailsValidator> _logger;

    public AuthorizationDetailsValidator(
        IOptionsMonitor<ProtectedResourceOptions> optionsMonitor,
        ILogger<AuthorizationDetailsValidator> logger)
    {
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    /// <summary>
    /// </summary>
    /// <param name="authenticationScheme">The authentication scheme name for options lookup.</param>
    public AuthorizationDetailsValidationResult ValidateAuthorizationDetails(HttpContext context, string authenticationScheme)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticationScheme);

        var options = _optionsMonitor.Get(authenticationScheme);
        if (options?.Metadata?.AuthorizationDetailsTypesSupported?.Any() != true)
        {
            _logger.LogDebug("No authorization details types supported for scheme: {Scheme}", authenticationScheme);
            return AuthorizationDetailsValidationResult.Success();
        }

        var authorizationDetails = ExtractAuthorizationDetails(context);
        if (authorizationDetails == null || !authorizationDetails.Any())
        {
            _logger.LogDebug("No authorization details found in request");
            return AuthorizationDetailsValidationResult.Success();
        }

        var validationErrors = new List<string>();
        foreach (var detail in authorizationDetails)
        {
            var detailValidation = ValidateAuthorizationDetail(detail, options.Metadata.AuthorizationDetailsTypesSupported);
            if (!detailValidation.IsValid)
            {
                validationErrors.AddRange(detailValidation.Errors);
            }
        }

        if (validationErrors.Any())
        {
            _logger.LogWarning("Authorization details validation failed: {Errors}", string.Join("; ", validationErrors));
            return AuthorizationDetailsValidationResult.Failure(validationErrors);
        }

        _logger.LogDebug("Authorization details validation successful");
        return AuthorizationDetailsValidationResult.Success();
    }

    /// <summary>
    /// </summary>
    private List<AuthorizationDetail>? ExtractAuthorizationDetails(HttpContext context)
    {
        if (context.Request.Query.TryGetValue("authorization_details", out var queryValue))
        {
            return ParseAuthorizationDetailsJson(queryValue.FirstOrDefault());
        }

        if (context.Request.HasFormContentType && 
            context.Request.Form.TryGetValue("authorization_details", out var formValue))
        {
            return ParseAuthorizationDetailsJson(formValue.FirstOrDefault());
        }

        var authorizationDetailsClaim = context.User?.FindFirst("authorization_details");
        if (authorizationDetailsClaim != null)
        {
            return ParseAuthorizationDetailsJson(authorizationDetailsClaim.Value);
        }

        return null;
    }

    /// <summary>
    /// </summary>
    private List<AuthorizationDetail>? ParseAuthorizationDetailsJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            var details = JsonSerializer.Deserialize<List<AuthorizationDetail>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });

            return details;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse authorization details JSON: {Json}", json);
            return null;
        }
    }

    /// <summary>
    /// </summary>
    private AuthorizationDetailValidationResult ValidateAuthorizationDetail(AuthorizationDetail detail, List<string> supportedTypes)
    {
        var errors = new List<string>();

        if (string.IsNullOrEmpty(detail.Type))
        {
            errors.Add("Authorization detail must have a 'type' field");
        }
        else if (!supportedTypes.Contains(detail.Type, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"Authorization detail type '{detail.Type}' is not supported. Supported types: {string.Join(", ", supportedTypes)}");
        }

        if (detail.Locations?.Any() == true)
        {
            foreach (var location in detail.Locations)
            {
                if (string.IsNullOrEmpty(location))
                {
                    errors.Add("Authorization detail location cannot be empty");
                }
                else if (!Uri.TryCreate(location, UriKind.Absolute, out var locationUri))
                {
                    errors.Add($"Authorization detail location '{location}' is not a valid URI");
                }
            }
        }

        if (detail.Actions?.Any() == true)
        {
            foreach (var action in detail.Actions)
            {
                if (string.IsNullOrEmpty(action))
                {
                    errors.Add("Authorization detail action cannot be empty");
                }
            }
        }

        if (detail.Datatypes?.Any() == true)
        {
            foreach (var datatype in detail.Datatypes)
            {
                if (string.IsNullOrEmpty(datatype))
                {
                    errors.Add("Authorization detail datatype cannot be empty");
                }
            }
        }

        if (!string.IsNullOrEmpty(detail.Identifier))
        {
            if (string.IsNullOrWhiteSpace(detail.Identifier))
            {
                errors.Add("Authorization detail identifier cannot be whitespace only");
            }
        }

        return errors.Any() 
            ? AuthorizationDetailValidationResult.Failure(errors)
            : AuthorizationDetailValidationResult.Success();
    }
}

/// <summary>
/// </summary>
public class AuthorizationDetail
{
    /// <summary>
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// </summary>
    public List<string>? Locations { get; set; }

    /// <summary>
    /// </summary>
    public List<string>? Actions { get; set; }

    /// <summary>
    /// </summary>
    public List<string>? Datatypes { get; set; }

    /// <summary>
    /// </summary>
    public string? Identifier { get; set; }

    /// <summary>
    /// </summary>
    public Dictionary<string, object>? AdditionalProperties { get; set; }
}

/// <summary>
/// </summary>
public class AuthorizationDetailsValidationResult
{
    public bool IsValid { get; private set; }
    public List<string> Errors { get; private set; } = new();

    private AuthorizationDetailsValidationResult(bool isValid, List<string>? errors = null)
    {
        IsValid = isValid;
        Errors = errors ?? new List<string>();
    }

    public static AuthorizationDetailsValidationResult Success() => new(true);
    public static AuthorizationDetailsValidationResult Failure(List<string> errors) => new(false, errors);
}

/// <summary>
/// </summary>
public class AuthorizationDetailValidationResult
{
    public bool IsValid { get; private set; }
    public List<string> Errors { get; private set; } = new();

    private AuthorizationDetailValidationResult(bool isValid, List<string>? errors = null)
    {
        IsValid = isValid;
        Errors = errors ?? new List<string>();
    }

    public static AuthorizationDetailValidationResult Success() => new(true);
    public static AuthorizationDetailValidationResult Failure(List<string> errors) => new(false, errors);
}
