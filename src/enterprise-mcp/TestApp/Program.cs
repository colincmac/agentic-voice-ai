using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Azure.Identity;
using Azure.Security.KeyVault.Keys;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Showcase.Authentication;
using Showcase.Authentication.AspNetCore.ResourceServer.Authentication;
using Showcase.Authentication.AspNetCore.ResourceServer.Endpoints;
using Showcase.Authentication.AspNetCore.ResourceServer.KeySigning;
using Showcase.Authentication.Core;
using JsonWebKey = Microsoft.IdentityModel.Tokens.JsonWebKey;
using JwtConstants = Microsoft.IdentityModel.JsonWebTokens.JwtConstants;
using JwtHeaderParameterNames = Microsoft.IdentityModel.JsonWebTokens.JwtHeaderParameterNames;


var keyClient = new KeyClient(new Uri("https://wdg-mcp-eastus2.vault.azure.net/"), new DefaultAzureCredential());

var cryptoClient = keyClient.GetCryptographyClient("mcp-protected-resource");
var keyVaultKey = await keyClient.GetKeyAsync("mcp-protected-resource");


var alg = SecurityAlgorithms.RsaSha256;

var metadata = new ProtectedResourceMetadata
{
    Resource = new Uri("https://example.com"),
    AuthorizationServers = new List<Uri>
    {
        new Uri("https://auth.example.com")
    },
    BearerMethodsSupported = new List<string> { "header", "body", "query" },
    ScopesSupported = new List<string> { "read", "write" },
    JwksUri = new Uri("https://example.com/.well-known/jwks.json"),
    ResourceSigningAlgValuesSupported = new List<string> { alg },
    ResourceName = "Example Protected Resource",
    ResourceDocumentation = new Uri("https://example.com/docs"),
    ResourcePolicyUri = new Uri("https://example.com/policy"),
    ResourceTosUri = new Uri("https://example.com/tos"),
};
Console.WriteLine(JsonSerializer.Serialize(metadata, JsonContext.Default.ProtectedResourceMetadata));

var claims = metadata.ToClaims() ?? throw new InvalidOperationException("Metadata payload cannot be null.");

var header = new JwtHeader
        {
            { JwtHeaderParameterNames.Typ, JwtConstants.HeaderType },
            { JwtHeaderParameterNames.Alg, alg }
        };
var payload = new JwtPayload(metadata.Resource?.ToString(), metadata.Resource?.ToString(), claims, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), DateTime.UtcNow);


var unsignedTokenData = header.Base64UrlEncode() + "." + payload.Base64UrlEncode();
var signResult = await cryptoClient.SignDataAsync(alg, Encoding.UTF8.GetBytes(unsignedTokenData));
var tokenValue = unsignedTokenData + "." + Base64UrlEncoder.Encode(signResult.Signature);
var verified = await cryptoClient.VerifyDataAsync(alg, Encoding.UTF8.GetBytes(unsignedTokenData), signResult.Signature);



Console.WriteLine("Press any key to exit...");
Console.ReadKey();

