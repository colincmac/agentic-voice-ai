
using Showcase.Authentication.Core;
using Showcase.Authentication.AspNetCore.ResourceServer.KeySigning;
using System.ComponentModel;

namespace Showcase.Authentication.AspNetCore.ResourceServer.Authentication;
/// <summary>
/// Configuration options for fetching and caching protected resource metadata.
/// </summary>
public sealed class ProtectedResourceOptions
{

    public ProtectedResourceMetadata Metadata { get; set; } = new();

    /// <summary>
    /// Gets or sets the discovery endpoint for obtaining metadata
    /// </summary>
    public Uri ProtectedResourceMetadataAddress { get; set; } = new Uri(ProtectedResourceConstants.DefaultOAuthProtectedResourcePathSuffix, UriKind.Relative);

    /// <summary>
    /// Gets or sets if HTTPS is required for the metadata address or authority.
    /// The default is true. This should be disabled only in development environments.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    public bool EnableMetadataHealthCheck { get; set; } = false;

    /// <summary>
    /// Options to help configure the <see cref="ISignedProtectedResourceIssuer">ISignedProtectedResourceIssuer</see> be used to sign the metadata token and provide the public keys for the jwks_url for signed_metadata validation.
    /// </summary>
    public JwksProviderOptions? JwksProvider { get; set; }

}


