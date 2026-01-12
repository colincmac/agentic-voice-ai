using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Microsoft.IdentityModel.Tokens;
using Showcase.Authentication.Core;

namespace Showcase.Authentication.AspNetCore.ResourceServer.KeySigning;

[JsonDerivedType(typeof(KeyVaultJwksProviderOptions), nameof(KeyVaultJwksProviderOptions.Provider))]
public class JwksProviderOptions : MetadataCacheOptions
{
    public virtual string Provider { get; set; } = string.Empty;
    public bool EnableHealthCheck { get; set; } = false;
    public string SigningAlgorithm { get; set; } = SecurityAlgorithms.RsaSha256;
}

public class KeyVaultJwksProviderOptions : JwksProviderOptions
{
    public override string Provider => "KeyVault";
    public string? VaultUri { get; set; }

    public string? KeyName { get; set; }
    public string? CertificateName { get; set; }
    public string? Version { get; set; }

    [JsonIgnore]
    public TokenCredential AzureTokenCredential { get; set; } = new DefaultAzureCredential();
}
