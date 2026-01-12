using Showcase.EnterpriseMcp.Server.Models;

namespace Showcase.EnterpriseMcp.Server.Services;

public interface IToolMonitor
{
    void Sanitize(ToolOutput output);
    void Log(string userId, string toolName, object input, ToolOutput output);
}

public class ToolMonitor : IToolMonitor
{
    private readonly ILogger<ToolMonitor> _logger;
    public ToolMonitor(ILogger<ToolMonitor> logger) => _logger = logger;

    public void Sanitize(ToolOutput output)
    {
        // TODO: Implement sanitization logic (e.g., strip dangerous patterns)
    }

    public void Log(string userId, string toolName, object input, ToolOutput output)
    {
        _logger.LogInformation("Tool invoked: {ToolName} by {UserId}", toolName, userId);
        // TODO: Log parameters and result to Azure Monitor or Application Insights
    }
}
