using System.Collections.Concurrent;

namespace Showcase.EnterpriseMcp.Server.Services;

public interface IToolSchemaRegistry
{
    string GetInputSchema(string toolName);
    string GetOutputSchema(string toolName);
}

public class ToolSchemaRegistry : IToolSchemaRegistry
{
    private readonly ConcurrentDictionary<string, (string InputSchema, string OutputSchema)> _schemas;

    public ToolSchemaRegistry()
    {
        _schemas = new ConcurrentDictionary<string, (string, string)>();
        // TODO: Load schemas from files or configuration
    }

    public string GetInputSchema(string toolName) => _schemas.TryGetValue(toolName, out var s) ? s.InputSchema : string.Empty;
    public string GetOutputSchema(string toolName) => _schemas.TryGetValue(toolName, out var s) ? s.OutputSchema : string.Empty;
}
