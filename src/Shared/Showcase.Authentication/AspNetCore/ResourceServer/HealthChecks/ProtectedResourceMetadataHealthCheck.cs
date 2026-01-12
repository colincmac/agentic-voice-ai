using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Showcase.Authentication.AspNetCore.ResourceServer.Authentication;
using Showcase.Authentication.Core;
using System.Text.Json;

namespace Showcase.Authentication.AspNetCore.ResourceServer.HealthChecks;

/// <summary>
/// </summary>
public class ProtectedResourceMetadataHealthCheck : IHealthCheck
{
    private readonly IOptionsMonitor<ProtectedResourceOptions> _optionsMonitor;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProtectedResourceMetadataHealthCheck> _logger;
    private readonly string _authenticationScheme;

    public ProtectedResourceMetadataHealthCheck(
        IOptionsMonitor<ProtectedResourceOptions> optionsMonitor,
        HttpClient httpClient,
        ILogger<ProtectedResourceMetadataHealthCheck> logger,
        string authenticationScheme = "Bearer")
    {
        _optionsMonitor = optionsMonitor;
        _httpClient = httpClient;
        _logger = logger;
        _authenticationScheme = authenticationScheme;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var options = _optionsMonitor.GetKeyedOrCurrent(_authenticationScheme);
            if (options == null)
            {
                _logger.LogWarning("No ProtectedResourceOptions found for scheme: {Scheme}", _authenticationScheme);
                return HealthCheckResult.Unhealthy($"No configuration found for authentication scheme: {_authenticationScheme}");
            }

            if (!options.EnableMetadataHealthCheck)
            {
                _logger.LogDebug("Metadata health check is disabled for scheme: {Scheme}", _authenticationScheme);
                return HealthCheckResult.Healthy("Health check disabled");
            }

            var metadataEndpointResult = await CheckMetadataEndpointAsync(options, cancellationToken);
            if (metadataEndpointResult.Status != HealthStatus.Healthy)
            {
                return metadataEndpointResult;
            }

            var jwksEndpointResult = await CheckJwksEndpointAsync(options, cancellationToken);
            if (jwksEndpointResult.Status != HealthStatus.Healthy)
            {
                return jwksEndpointResult;
            }

            var authServerResult = await CheckAuthorizationServersAsync(options, cancellationToken);
            if (authServerResult.Status != HealthStatus.Healthy)
            {
                return authServerResult;
            }

            _logger.LogDebug("Protected resource metadata health check passed for scheme: {Scheme}", _authenticationScheme);
            return HealthCheckResult.Healthy("All metadata endpoints are accessible and valid");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Protected resource metadata health check failed for scheme: {Scheme}", _authenticationScheme);
            return HealthCheckResult.Unhealthy($"Health check failed: {ex.Message}", ex);
        }
    }

    private async Task<HealthCheckResult> CheckMetadataEndpointAsync(ProtectedResourceOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var metadataUri = options.ProtectedResourceMetadataAddress.IsAbsoluteUri
                ? options.ProtectedResourceMetadataAddress
                : new Uri("https://localhost" + options.ProtectedResourceMetadataAddress.ToString()); // Fallback for relative URIs

            _logger.LogDebug("Checking metadata endpoint: {Uri}", metadataUri);

            var response = await _httpClient.GetAsync(metadataUri, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Metadata endpoint returned status code: {StatusCode}", response.StatusCode);
                return HealthCheckResult.Degraded($"Metadata endpoint returned {response.StatusCode}");
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Metadata endpoint returned unexpected content type: {ContentType}", contentType);
                return HealthCheckResult.Degraded($"Unexpected content type: {contentType}");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var metadata = JsonSerializer.Deserialize<ProtectedResourceMetadata>(content, JsonContext.Default.Options);
            
            if (metadata?.Resource == null)
            {
                _logger.LogWarning("Metadata endpoint returned invalid metadata structure");
                return HealthCheckResult.Degraded("Invalid metadata structure");
            }

            _logger.LogDebug("Metadata endpoint check passed");
            return HealthCheckResult.Healthy("Metadata endpoint accessible and valid");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to connect to metadata endpoint");
            return HealthCheckResult.Unhealthy($"Cannot connect to metadata endpoint: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse metadata response");
            return HealthCheckResult.Degraded($"Invalid JSON response from metadata endpoint: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error checking metadata endpoint");
            return HealthCheckResult.Unhealthy($"Metadata endpoint check failed: {ex.Message}");
        }
    }

    private async Task<HealthCheckResult> CheckJwksEndpointAsync(ProtectedResourceOptions options, CancellationToken cancellationToken)
    {
        try
        {
            if (options.Metadata.JwksUri == null)
            {
                _logger.LogDebug("No JWKS URI configured, skipping JWKS endpoint check");
                return HealthCheckResult.Healthy("No JWKS endpoint configured");
            }

            _logger.LogDebug("Checking JWKS endpoint: {Uri}", options.Metadata.JwksUri);

            var response = await _httpClient.GetAsync(options.Metadata.JwksUri, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("JWKS endpoint returned status code: {StatusCode}", response.StatusCode);
                return HealthCheckResult.Degraded($"JWKS endpoint returned {response.StatusCode}");
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("JWKS endpoint returned unexpected content type: {ContentType}", contentType);
                return HealthCheckResult.Degraded($"JWKS endpoint unexpected content type: {contentType}");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var jwks = JsonSerializer.Deserialize<JwksDocument>(content, JsonContext.Default.Options);
            
            if (jwks?.Keys?.Any() != true)
            {
                _logger.LogWarning("JWKS endpoint returned no keys");
                return HealthCheckResult.Degraded("JWKS endpoint contains no keys");
            }

            _logger.LogDebug("JWKS endpoint check passed");
            return HealthCheckResult.Healthy("JWKS endpoint accessible and valid");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to connect to JWKS endpoint");
            return HealthCheckResult.Degraded($"Cannot connect to JWKS endpoint: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse JWKS response");
            return HealthCheckResult.Degraded($"Invalid JSON response from JWKS endpoint: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error checking JWKS endpoint");
            return HealthCheckResult.Degraded($"JWKS endpoint check failed: {ex.Message}");
        }
    }

    private async Task<HealthCheckResult> CheckAuthorizationServersAsync(ProtectedResourceOptions options, CancellationToken cancellationToken)
    {
        try
        {
            if (options.Metadata.AuthorizationServers?.Any() != true)
            {
                _logger.LogDebug("No authorization servers configured, skipping connectivity check");
                return HealthCheckResult.Healthy("No authorization servers configured");
            }

            var failedServers = new List<string>();
            var degradedServers = new List<string>();

            foreach (var authServer in options.Metadata.AuthorizationServers)
            {
                try
                {
                    _logger.LogDebug("Checking authorization server connectivity: {Uri}", authServer);

                    var wellKnownUri = new Uri(authServer, ".well-known/openid_configuration");
                    
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(10)); // 10 second timeout per server

                    var response = await _httpClient.GetAsync(wellKnownUri, cts.Token);
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Authorization server {Server} returned status code: {StatusCode}", authServer, response.StatusCode);
                        degradedServers.Add($"{authServer} ({response.StatusCode})");
                    }
                    else
                    {
                        _logger.LogDebug("Authorization server {Server} connectivity check passed", authServer);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw; // Re-throw if the main cancellation token was cancelled
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Authorization server {Server} connectivity check timed out", authServer);
                    degradedServers.Add($"{authServer} (timeout)");
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Failed to connect to authorization server: {Server}", authServer);
                    failedServers.Add($"{authServer} ({ex.Message})");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error checking authorization server: {Server}", authServer);
                    failedServers.Add($"{authServer} ({ex.GetType().Name})");
                }
            }

            if (failedServers.Any())
            {
                var message = $"Authorization server connectivity failed: {string.Join(", ", failedServers)}";
                if (degradedServers.Any())
                {
                    message += $"; Degraded: {string.Join(", ", degradedServers)}";
                }
                return HealthCheckResult.Unhealthy(message);
            }

            if (degradedServers.Any())
            {
                var message = $"Authorization server connectivity degraded: {string.Join(", ", degradedServers)}";
                return HealthCheckResult.Degraded(message);
            }

            _logger.LogDebug("All authorization servers connectivity check passed");
            return HealthCheckResult.Healthy("All authorization servers are accessible");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error checking authorization servers");
            return HealthCheckResult.Unhealthy($"Authorization servers check failed: {ex.Message}");
        }
    }
}
