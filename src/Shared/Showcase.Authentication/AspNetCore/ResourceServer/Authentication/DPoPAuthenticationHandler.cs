using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Showcase.Authentication.Core;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Showcase.Authentication.AspNetCore.ResourceServer.Authentication;

/// <summary>
/// </summary>
public class DPoPAuthenticationHandler : AuthenticationHandler<DPoPAuthenticationOptions>
{
    private readonly JsonWebTokenHandler _tokenHandler;

    public DPoPAuthenticationHandler(
        IOptionsMonitor<DPoPAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
        _tokenHandler = new JsonWebTokenHandler();
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("DPoP", out var dpopHeader) || dpopHeader.Count != 1)
        {
            Logger.LogDebug("DPoP header not found or multiple values present");
            return AuthenticateResult.NoResult();
        }

        var dpopProof = dpopHeader.First();
        if (string.IsNullOrEmpty(dpopProof))
        {
            Logger.LogDebug("DPoP header is empty");
            return AuthenticateResult.NoResult();
        }

        try
        {
            var validationResult = await ValidateDPoPProofAsync(dpopProof);
            if (!validationResult.IsValid)
            {
                Logger.LogWarning("DPoP proof validation failed: {Error}", validationResult.Exception?.Message);
                return AuthenticateResult.Fail("Invalid DPoP proof");
            }

            var claims = ExtractClaimsFromDPoPProof(validationResult.SecurityToken as JsonWebToken);
            
            if (Options.RequireDPoPBoundAccessTokens)
            {
                var accessTokenValidation = await ValidateDPoPBoundAccessTokenAsync();
                if (!accessTokenValidation)
                {
                    Logger.LogWarning("DPoP-bound access token validation failed");
                    return AuthenticateResult.Fail("DPoP-bound access token required");
                }
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            Logger.LogDebug("DPoP authentication successful");
            return AuthenticateResult.Success(ticket);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during DPoP authentication");
            return AuthenticateResult.Fail("DPoP authentication error");
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        
        var challengeHeader = $"{ProtectedResourceConstants.AuthenticationSchemes.DPoP} error=\"use_dpop_nonce\"";
        Response.Headers.Append("WWW-Authenticate", challengeHeader);
        
        Logger.LogDebug("DPoP challenge issued");
        return Task.CompletedTask;
    }

    private async Task<TokenValidationResult> ValidateDPoPProofAsync(string dpopProof)
    {
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = Options.ClockSkew,
            RequireSignedTokens = true,
            RequireExpirationTime = false
        };

        var jwt = new JsonWebToken(dpopProof);
        
        if (!ValidateDPoPProofStructure(jwt))
        {
            return new TokenValidationResult
            {
                IsValid = false,
                Exception = new SecurityTokenValidationException("Invalid DPoP proof structure")
            };
        }

        var jwk = ExtractPublicKeyFromJwt(jwt);
        if (jwk == null)
        {
            return new TokenValidationResult
            {
                IsValid = false,
                Exception = new SecurityTokenValidationException("Unable to extract public key from DPoP proof")
            };
        }

        validationParameters.IssuerSigningKey = jwk;

        var result = await _tokenHandler.ValidateTokenAsync(dpopProof, validationParameters);
        
        if (result.IsValid)
        {
            result = ValidateDPoPSpecificClaims(result);
        }

        return result;
    }

    private bool ValidateDPoPProofStructure(JsonWebToken jwt)
    {
        if (!jwt.TryGetHeaderValue("typ", out string? typ) || typ != "dpop+jwt")
        {
            Logger.LogDebug("DPoP proof missing or invalid 'typ' header");
            return false;
        }

        if (!jwt.TryGetHeaderValue("alg", out string? alg) || alg == null || !Options.SupportedAlgorithms.Contains(alg))
        {
            Logger.LogDebug("DPoP proof has unsupported algorithm: {Algorithm}", alg);
            return false;
        }

        if (!jwt.TryGetClaim("jti", out _))
        {
            Logger.LogDebug("DPoP proof missing 'jti' claim");
            return false;
        }

        if (!jwt.TryGetClaim("htm", out _))
        {
            Logger.LogDebug("DPoP proof missing 'htm' claim");
            return false;
        }

        if (!jwt.TryGetClaim("htu", out _))
        {
            Logger.LogDebug("DPoP proof missing 'htu' claim");
            return false;
        }

        return true;
    }

    private SecurityKey? ExtractPublicKeyFromJwt(JsonWebToken jwt)
    {
        try
        {
            if (jwt.TryGetHeaderValue("jwk", out object? jwkObj))
            {
                var jwkJson = JsonSerializer.Serialize(jwkObj);
                var jwk = new JsonWebKey(jwkJson);
                return jwk;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to extract public key from DPoP proof");
        }

        return null;
    }

    private TokenValidationResult ValidateDPoPSpecificClaims(TokenValidationResult result)
    {
        var jwt = result.SecurityToken as JsonWebToken;
        if (jwt == null)
        {
            return new TokenValidationResult
            {
                IsValid = false,
                Exception = new SecurityTokenValidationException("Invalid token type")
            };
        }

        if (jwt.TryGetClaim("htm", out var htm) && htm.ToString() != Request.Method)
        {
            Logger.LogDebug("DPoP proof 'htm' claim mismatch. Expected: {Expected}, Actual: {Actual}", 
                Request.Method, htm);
            return new TokenValidationResult
            {
                IsValid = false,
                Exception = new SecurityTokenValidationException("HTTP method mismatch in DPoP proof")
            };
        }

        if (jwt.TryGetClaim("htu", out var htu))
        {
            var expectedUri = $"{Request.Scheme}://{Request.Host}{Request.Path}";
            if (htu.ToString() != expectedUri)
            {
                Logger.LogDebug("DPoP proof 'htu' claim mismatch. Expected: {Expected}, Actual: {Actual}", 
                    expectedUri, htu);
                return new TokenValidationResult
                {
                    IsValid = false,
                    Exception = new SecurityTokenValidationException("HTTP URI mismatch in DPoP proof")
                };
            }
        }

        if (jwt.TryGetClaim("iat", out var iat))
        {
            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(iat));
            var age = DateTimeOffset.UtcNow - issuedAt;
            if (age.TotalSeconds > Options.MaxProofAge)
            {
                Logger.LogDebug("DPoP proof is too old. Age: {Age} seconds, Max: {Max} seconds", 
                    age.TotalSeconds, Options.MaxProofAge);
                return new TokenValidationResult
                {
                    IsValid = false,
                    Exception = new SecurityTokenValidationException("DPoP proof is too old")
                };
            }
        }

        return result;
    }

    private async Task<bool> ValidateDPoPBoundAccessTokenAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            return false;
        }

        var authValue = authHeader.FirstOrDefault();
        if (string.IsNullOrEmpty(authValue) || !authValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var accessToken = authValue.Substring("Bearer ".Length);
        
        try
        {
            var jwt = new JsonWebToken(accessToken);
            
            if (jwt.TryGetClaim("cnf", out var cnf))
            {
                var cnfJson = JsonSerializer.Deserialize<JsonElement>(cnf.ToString() ?? "{}");
                if (cnfJson.TryGetProperty("jkt", out var jkt))
                {
                    return await ValidateJwkThumbprintAsync(jkt.GetString());
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to validate DPoP-bound access token");
        }

        return false;
    }

    private Task<bool> ValidateJwkThumbprintAsync(string? expectedThumbprint)
    {
        if (string.IsNullOrEmpty(expectedThumbprint))
        {
            return Task.FromResult(false);
        }

        if (!Request.Headers.TryGetValue("DPoP", out var dpopHeader))
        {
            return Task.FromResult(false);
        }

        try
        {
            var jwt = new JsonWebToken(dpopHeader.First());
            if (jwt.TryGetHeaderValue<object>("jwk", out var jwkObj))
            {
                var jwkJson = JsonSerializer.Serialize(jwkObj);
                var jwk = new JsonWebKey(jwkJson);
                var thumbprint = Convert.ToBase64String(jwk.ComputeJwkThumbprint());
                
                return Task.FromResult(string.Equals(thumbprint, expectedThumbprint, StringComparison.Ordinal));
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to validate JWK thumbprint");
        }

        return Task.FromResult(false);
    }

    private List<Claim> ExtractClaimsFromDPoPProof(JsonWebToken? jwt)
    {
        var claims = new List<Claim>();

        if (jwt == null)
        {
            return claims;
        }

        if (jwt.TryGetClaim("jti", out var jti))
        {
            claims.Add(new Claim("jti", jti.ToString() ?? ""));
        }

        if (jwt.TryGetClaim("htm", out var htm))
        {
            claims.Add(new Claim("htm", htm.ToString() ?? ""));
        }

        if (jwt.TryGetClaim("htu", out var htu))
        {
            claims.Add(new Claim("htu", htu.ToString() ?? ""));
        }

        if (jwt.TryGetClaim("iat", out var iat))
        {
            claims.Add(new Claim("iat", iat.ToString() ?? ""));
        }

        claims.Add(new Claim("auth_method", "dpop"));

        return claims;
    }
}
