using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;
using Showcase.Authentication.AspNetCore.ResourceServer.Authentication;
using Showcase.Authentication.AspNetCore.ResourceServer.KeySigning;
using Showcase.Authentication.AspNetCore.ResourceServer.Authorization;

namespace Showcase.Authentication.Core;

/// <summary>Source-generated JSON type information.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(ProtectedResourceMetadata))]
[JsonSerializable(typeof(JwksDocument))]
[JsonSerializable(typeof(PublicJsonWebKeyParameters))]
[JsonSerializable(typeof(ProtectedResourceOptions))]
[JsonSerializable(typeof(JwksProviderOptions))]
[JsonSerializable(typeof(AzureKeyVaultProtectedResourceIssuer))]
[JsonSerializable(typeof(DPoPAuthenticationOptions))]
[JsonSerializable(typeof(AuthorizationDetail))]
[JsonSerializable(typeof(AuthorizationDetailsValidationResult))]
[JsonSerializable(typeof(AuthorizationDetailValidationResult))]
[JsonSerializable(typeof(List<AuthorizationDetail>))]
public partial class JsonContext : JsonSerializerContext;

