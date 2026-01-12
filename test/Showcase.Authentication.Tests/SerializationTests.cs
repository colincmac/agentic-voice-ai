using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Showcase.Authentication.AspNetCore.ResourceServer.Authentication;
using Showcase.Authentication.AspNetCore.ResourceServer.KeySigning;
using Showcase.Authentication.Tests.Helpers;

namespace Showcase.Authentication.Tests;
public class SerializationTests
{
    public const string OptionsWithJwksProvider = """

        """;
    [Fact]
    public void ProtectedResourceOptions_SerializationTest()
    {
        // Arrange
        var options = new ProtectedResourceOptions
        {
            ProtectedResourceMetadataAddress = new Uri("https://example.com/metadata"),
            RequireHttpsMetadata = true,
            EnableMetadataHealthCheck = true,
            JwksProvider = new KeyVaultJwksProviderOptions()
            {
                VaultUri = JwksProviderHelpers.KeyVaultUri,
                KeyName = JwksProviderHelpers.KeyVaultKeyName,
            }
        };

        // Act
        var serialized = System.Text.Json.JsonSerializer.Serialize(options);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<ProtectedResourceOptions>(serialized);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(options.ProtectedResourceMetadataAddress, deserialized.ProtectedResourceMetadataAddress);
        Assert.Equal(options.RequireHttpsMetadata, deserialized.RequireHttpsMetadata);
        Assert.Equal(options.EnableMetadataHealthCheck, deserialized.EnableMetadataHealthCheck);
        Assert.IsType<KeyVaultJwksProviderOptions>(deserialized.JwksProvider);
        Assert.Equal(JwksProviderHelpers.KeyVaultUri, ((KeyVaultJwksProviderOptions)deserialized.JwksProvider).VaultUri);
        Assert.Equal(JwksProviderHelpers.KeyVaultKeyName, ((KeyVaultJwksProviderOptions)deserialized.JwksProvider).KeyName);
    }
}
