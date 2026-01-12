using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Showcase.Authentication.Core;
internal interface IResourceProviderEndpoints
{
    /// <summary>
    /// URL of the protected resource's JSON Web Key (JWK) Set document.
    /// </summary>
    /// <remarks>
    /// OPTIONAL. This contains public keys belonging to the protected resource, such as signing key(s)
    /// that the resource server uses to sign resource responses. This URL MUST use the https scheme.
    /// </remarks>
    public Uri? JwksUri { get; set; }

    /// <summary>
    /// The URI to the resource documentation.
    /// </summary>
    /// <remarks>
    /// OPTIONAL. URL of a page containing human-readable information that developers might want or need to know
    /// when using the protected resource.
    /// </remarks>
    [JsonPropertyName("resource_documentation")]
    public Uri? ResourceDocumentation { get; set; }

    /// <summary>
    /// URL of a page containing human-readable information about the protected resource's requirements.
    /// </summary>
    /// <remarks>
    /// OPTIONAL. Information about how the client can use the data provided by the protected resource.
    /// </remarks>
    [JsonPropertyName("resource_policy_uri")]
    public Uri? ResourcePolicyUri { get; set; }

    /// <summary>
    /// URL of a page containing human-readable information about the protected resource's terms of service.
    /// </summary>
    /// <remarks>
    /// OPTIONAL. The value of this field MAY be internationalized.
    /// </remarks>
    [JsonPropertyName("resource_tos_uri")]
    public Uri? ResourceTosUri { get; set; }
}
