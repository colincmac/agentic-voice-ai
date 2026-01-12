using Microsoft.AspNetCore.Authentication;

namespace Showcase.Authentication.AspNetCore.ResourceServer.Authentication;

/// <summary>
/// </summary>
public class DPoPAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// </summary>
    public List<string> SupportedAlgorithms { get; set; } = new() { "RS256", "ES256" };

    /// <summary>
    /// </summary>
    public int MaxProofAge { get; set; } = 60;

    /// <summary>
    /// </summary>
    public bool RequireDPoPBoundAccessTokens { get; set; } = false;

    /// <summary>
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);
}
