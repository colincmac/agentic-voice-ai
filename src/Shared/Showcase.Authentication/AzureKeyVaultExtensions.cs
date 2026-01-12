using Microsoft.IdentityModel.Tokens;
using Showcase.Authentication.Core;
using KeyVaultKey = Azure.Security.KeyVault.Keys.KeyVaultKey;

namespace Showcase.Authentication;
public static class AzureKeyVaultExtensions
{

    public static PublicJsonWebKeyParameters ToPublicJwk(this KeyVaultKey keyVaultKey, string defaultUseValue = JsonWebKeyUseNames.Sig)
    {
        return new PublicJsonWebKeyParameters
        {
 
            KeyType = keyVaultKey.KeyType.ToString(),
            KeyId = keyVaultKey.Properties.Version,
            N = keyVaultKey.Key.N,
            E = keyVaultKey.Key.E,
            CurveName = keyVaultKey.Key.CurveName?.ToString(),
            X = keyVaultKey.Key.X,
            Y = keyVaultKey.Key.Y,
            // The 'use' property is required for RFC 9728, when there are multiple keys in the JWK set.
            Use = KeyOpsToUse(keyVaultKey.Key.KeyOps.Select(op => op.ToString()), defaultUseValue)
        };
    }

    private static string? KeyOpsToUse(IEnumerable<string> keyOps, string defaultUse)
    {
        var sigOps = new HashSet<string> { "sign", "verify" };
        var encOps = new HashSet<string> { "encrypt", "decrypt", "wrapKey", "unwrapKey" };

        var hasSig = keyOps.Any(sigOps.Contains);
        var hasEnc = keyOps.Any(encOps.Contains);

        if (hasSig && !hasEnc)
            return JsonWebKeyUseNames.Sig;
        if (!hasSig && hasEnc)
            return JsonWebKeyUseNames.Enc;
        // If both or neither, do not map to a use value.
        return defaultUse;
    }
}
