using Microsoft.IdentityModel.JsonWebTokens;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Showcase.Authentication.Core;
public static class ProtectedResourceMetadataExtensions
{

    public static Claim[] ToClaims(this ProtectedResourceMetadata meta)
    {
        if (meta is null) throw new ArgumentNullException(nameof(meta));

        JsonNode? root = JsonSerializer.SerializeToNode(meta, JsonContext.Default.Options);

        if (root is not JsonObject obj)
            return [];

        if (meta.Resource is not null) obj[JwtRegisteredClaimNames.Iss] = meta.Resource.ToString();

        var claims = new List<Claim>();

        // 2) Walk every property
        foreach (var kv in obj)
        {
            string claimType = kv.Key;
            JsonNode? valueNode = kv.Value;
            if(claimType is null || valueNode is null)
                continue; // skip null values
            claims.Add(new Claim(claimType, valueNode.ToJsonString(JsonContext.Default.Options), JsonClaimValueTypes.JsonArray)); // Changed to add entire array as a single claim

            //if (valueNode is JsonArray arr)
            //{
            //}
            //else
            //{
            //    // single value (string, bool, number, nested object…)
            //    claims.Add(new Claim(claimType, valueNode?.ToJsonString() ?? ""));
            //}
        }

        return [.. claims];
    }
}
