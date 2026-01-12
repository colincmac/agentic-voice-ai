using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Azure;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Azure;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace Showcase.Authentication.Tests.Helpers;
public static class JwksProviderHelpers
{
    private static readonly RSA rsa = RSA.Create();
    public const string KeyVaultKeyVersion = "latest";
    public const string KeyVaultKeyName = "test-key-vault-key";
    public const string KeyVaultName = "test-key-vault";
    public const string KeyVaultUri = $"https://{KeyVaultName}.vault.azure.net/";
    public const string KeyVaultKeyId = $"https://{KeyVaultName}.vault.azure.net/keys/{KeyVaultKeyName}/{KeyVaultKeyVersion}";


    #region KeyVault Mocking Helpers
    public static KeyProperties GetKeyProperties()
    {
       var properties =  KeyModelFactory.KeyProperties(
        id: new Uri(KeyVaultKeyId),
        vaultUri: new Uri(KeyVaultUri),
        name: KeyVaultKeyName,
        version: KeyVaultKeyVersion,
        createdOn: DateTime.UtcNow.AddDays(-1)
        );
        properties.ExpiresOn = DateTime.UtcNow.AddDays(30);
        return properties;
    }
   
    public static Azure.Security.KeyVault.Keys.JsonWebKey KeyVaultJsonWebKey => KeyModelFactory.JsonWebKey(
        keyType: KeyType.Rsa,
        keyOps: ["Signing"],
        n: rsa.ExportRSAPublicKey(),
        e: rsa.ExportRSAPublicKey().Skip(256).Take(3).ToArray() // Simplified for testing purposes
    );

    public static KeyVaultKey KeyVaultKeyResult => KeyModelFactory.KeyVaultKey(GetKeyProperties(), KeyVaultJsonWebKey);

    public static CryptographyClient GetCryptographyClient => new CryptographyClient(KeyVaultJsonWebKey);

    public static Mock<CryptographyClient> GetCryptographyClientMock()
    {
        var mock = new Mock<CryptographyClient>();
        mock.Setup(m => m.SignDataAsync(It.IsAny<SignatureAlgorithm>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()).Result)
            .Returns(Response.FromValue(CryptographyModelFactory.SignResult(KeyVaultKeyId, [1, 2, 3], SignatureAlgorithm.RS256), Mock.Of<Response>()));

        return mock;
    }

    public static Mock<KeyClient> GetKeyClient()
    {
        var mock = new Mock<KeyClient>();
        mock.Setup(m => m.GetKeyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()).Result).Returns(Response.FromValue(KeyVaultKeyResult, Mock.Of<Response>()));
        mock.Setup(m => m.GetCryptographyClient(It.IsAny<string>(), It.IsAny<string>())).Returns(GetCryptographyClientMock().Object);
        return mock;
    }
    #endregion

}
public class AzureKeyClientFactoryMock : IAzureClientFactory<KeyClient>
{
    public KeyClient CreateClient(string name)
    {
        return JwksProviderHelpers.GetKeyClient().Object;
    }
}
