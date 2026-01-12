using Azure.Security.KeyVault.Keys;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Showcase.Authentication.AspNetCore.ResourceServer.Authentication;
using Showcase.Authentication.Core;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using JwtConstants = Microsoft.IdentityModel.JsonWebTokens.JwtConstants;
using JwtHeaderParameterNames = Microsoft.IdentityModel.JsonWebTokens.JwtHeaderParameterNames;

namespace Showcase.Authentication.AspNetCore.ResourceServer.KeySigning;
public class AzureKeyVaultProtectedResourceIssuer : ISignedProtectedResourceIssuer
{
    private string? _serviceKey;
    private readonly KeyClient _keyClient;
    private KeyVaultKey? _keyVaultKey;
    private JwksDocument? _jwksDocument;
    IOptionsMonitor<ProtectedResourceOptions> _optionsMonitor;

    public AzureKeyVaultProtectedResourceIssuer(IAzureClientFactory<KeyClient> keyClientFactory, IOptionsMonitor<ProtectedResourceOptions> optionsMonitor, [ServiceKey] string serviceKeyName)
    {
        _optionsMonitor = optionsMonitor;
        _serviceKey = serviceKeyName;
        _keyClient = keyClientFactory.CreateClient(serviceKeyName);
    }

    public async Task<JwksDocument> GetJwksDocumentAsync(CancellationToken cancellationToken = default)
    {

        if (_keyVaultKey?.Properties.ExpiresOn?.UtcDateTime > DateTime.UtcNow
            && _jwksDocument is not null
            && _jwksDocument.Keys.Count != 0) return _jwksDocument;

        var keyVaultKey = await GetOrSetCurrentKeyVaultKeyAsync(cancellationToken);

        var jwk = keyVaultKey.ToPublicJwk() ?? throw new InvalidOperationException("KeyVault key cannot be converted to JWK.");
        _jwksDocument = new JwksDocument([jwk]);

        return _jwksDocument;
    }

    /// <summary>
    /// Attempts to sign the provided protected resource metadata using the configured Azure Key Vault key and adds the signed metadata to the metadata object under `signed_metadata`.
    /// </summary>
    /// <param name="metadata"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<string> GetSignMetadataTokenAsync(ProtectedResourceMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata.Resource, "Protected resource metadata cannot be null.");

        var options = _optionsMonitor.GetKeyedOrCurrent(_serviceKey);

        if(options.JwksProvider is not KeyVaultJwksProviderOptions keyVaultOptions) 
        {
            throw new InvalidOperationException("JwksProviderOptions must be of type KeyVaultJwksProviderOptions to use Azure Key Vault signing.");
        }

        var cryptoClient = _keyClient.GetCryptographyClient(keyVaultOptions.KeyName, keyVaultOptions.Version);
        var key = await GetOrSetCurrentKeyVaultKeyAsync(cancellationToken);

        var metadataResource = metadata.Resource.ToString();
        var claims = metadata.ToClaims();
        var header = new JwtHeader
        {
            { JwtHeaderParameterNames.Typ, JwtConstants.HeaderType },
            { JwtHeaderParameterNames.Alg, keyVaultOptions.SigningAlgorithm }
        };

        var payload = new JwtPayload(
            issuer: metadataResource,
            audience: metadataResource,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: key.Properties.ExpiresOn?.UtcDateTime ?? DateTime.UtcNow.AddDays(1),
            issuedAt: DateTime.UtcNow);

        var unsignedTokenData = header.Base64UrlEncode() + "." + payload.Base64UrlEncode();

        var signResult = await cryptoClient.SignDataAsync(keyVaultOptions.SigningAlgorithm, Encoding.UTF8.GetBytes(unsignedTokenData), cancellationToken: cancellationToken);
        var tokenValue = unsignedTokenData + "." + Base64UrlEncoder.Encode(signResult.Signature);

        return tokenValue;
    }

    private async Task<KeyVaultKey> GetOrSetCurrentKeyVaultKeyAsync(CancellationToken cancellationToken = default)
    {
        if (_keyVaultKey is not null && _keyVaultKey.Properties.ExpiresOn > DateTime.UtcNow.AddMinutes(-1)) return _keyVaultKey;

        var options = _optionsMonitor.GetKeyedOrCurrent(_serviceKey);
        if (options.JwksProvider is not KeyVaultJwksProviderOptions keyVaultOptions)
        {
            throw new InvalidOperationException("JwksProviderOptions must be of type KeyVaultJwksProviderOptions to use Azure Key Vault signing.");
        }

        KeyVaultKey keyVaultKey = await _keyClient.GetKeyAsync(keyVaultOptions.KeyName, keyVaultOptions.Version, cancellationToken);
        _keyVaultKey = keyVaultKey;
        return _keyVaultKey;
    }

}
