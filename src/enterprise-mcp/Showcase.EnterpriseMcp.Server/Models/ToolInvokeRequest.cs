namespace Showcase.EnterpriseMcp.Server.Models;

public class ToolInvokeRequest
{
    public string ToolName { get; set; } = string.Empty;
    public object Input { get; set; } = new { };
    public string UserId { get; set; } = string.Empty;
}
