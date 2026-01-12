using Microsoft.AspNetCore.Authorization;

namespace Showcase.Authentication.ModelContextProtocol;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class AuthorizedToolAttribute : Attribute//, //IAuthorizeData
{
    // The “logical” resource key (e.g. “foo” or “bar”) that we will
    // turn into a well-known metadata URL later.
    public string Resource { get; }

    // The scopes required to call this endpoint.
    public string[] Scopes { get; }

    public AuthorizedToolAttribute(string resource, params string[] scopes)
    {
        Resource = resource;
        Scopes = scopes;
        Policy = $"ResourcePolicy::{resource}";
    }

    // ASP.NET Core will look at this “Policy” property and try to find
    // a matching AuthorizationPolicy. We’ll register one per resource.
    public string Policy { get; set; }
    public string Roles { get; set; } = string.Empty;
    public string? AuthenticationSchemes { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
}
