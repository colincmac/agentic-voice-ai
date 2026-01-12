using System.Text.Json.Serialization;
using Azure.Security.KeyVault.Keys;
using Microsoft.IdentityModel.Tokens;

namespace Showcase.Authentication.Core;

public sealed record JwksDocument([property: JsonPropertyName(JsonWebKeySetParameterNames.Keys)] ICollection<PublicJsonWebKeyParameters> Keys);

public sealed class PublicJsonWebKeyParameters
{
    [JsonPropertyName(JsonWebKeyParameterNames.Kid)]
    public required string KeyId { get; init; }

    [JsonPropertyName(JsonWebKeyParameterNames.Kty)]
    public required string KeyType { get; init; }

    /// <summary>
    /// Gets or sets the 'use' (Public Key Use)..
    /// </summary>
    [JsonPropertyName(JsonWebKeyParameterNames.Use)]
    public string? Use { get; set; }

    #region RSA Public Key Parameters

    /// <summary>
    /// Gets the RSA modulus.
    /// </summary>
    [JsonPropertyName(JsonWebKeyParameterNames.N)]
    [JsonConverter(typeof(Base64UrlConverter))]
    public byte[]? N { get; set; }

    /// <summary>
    /// Gets RSA public exponent.
    /// </summary>
    [JsonPropertyName(JsonWebKeyParameterNames.E)]
    [JsonConverter(typeof(Base64UrlConverter))]
    public byte[]? E { get; set; }

    #endregion

    #region EC Public Key Parameters

    /// <summary>
    /// Gets the name of the elliptical curve.
    /// </summary>
    [JsonPropertyName(JsonWebKeyParameterNames.Crv)]
    public string? CurveName { get; init; }

    /// <summary>
    /// Gets the X coordinate of the elliptic curve point.
    /// </summary>
    [JsonPropertyName(JsonWebKeyParameterNames.X)]
    [JsonConverter(typeof(Base64UrlConverter))]
    public byte[]? X { get; set; }

    /// <summary>
    /// Gets the Y coordinate for the elliptic curve point.
    /// </summary>
    [JsonPropertyName(JsonWebKeyParameterNames.Y)]
    [JsonConverter(typeof(Base64UrlConverter))]
    public byte[]? Y { get; set; }
    #endregion


    #region Certificate Parameters

    /// <summary>
    /// Gets the 'x5c' collection (X.509 Certificate Chain)..
    /// </summary>
    [JsonPropertyName(JsonWebKeyParameterNames.X5c)]
    public IList<string>? X5c;
    /// <summary>
    /// Gets or sets the 'x5t' (X.509 Certificate SHA-1 thumbprint)..
    /// </summary>
    [JsonPropertyName(JsonWebKeyParameterNames.X5t)]
    public string? X5t { get; set; }

    /// <summary>
    /// Gets or sets the 'x5t#S256' (X.509 Certificate SHA-256 thumbprint)..
    /// </summary>
    [JsonPropertyName(JsonWebKeyParameterNames.X5tS256)]
    public string? X5tS256 { get; set; }

    /// <summary>
    /// Gets or sets the 'x5u' (X.509 URL)..
    /// </summary>
    [JsonPropertyName(JsonWebKeyParameterNames.X5u)]
    public string? X5u { get; set; }

    #endregion
}
