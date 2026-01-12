namespace Showcase.EnterpriseMcp.Server.Models;

public class McpAuthOptions
{
    public const string SectionName = "McpAuth";

    public Uri[] AuthServers { get; set; } = []; // https://login.microsoftonline.com/my-tenant/v2.0
    public Uri? ResourceDocumentation { get; set; }
    public Uri ResourceUri { get; set; } = new Uri("http://localhost:7071"); // Changed from HTTPS to HTTP for local development
    public string[]? SupportedBearerMethod { get; set; } = ["header"];
    public string[] SupportedScopes { get; set; } = ["mcp.tools", "mcp.prompts", "mcp.resources"];

}
