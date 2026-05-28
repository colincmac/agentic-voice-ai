using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Agents.AI.ContactCenter.IvrWorkflow.Loading;

/// <summary>
/// Source that reads inline YAML documents from <see cref="IConfiguration"/> under a
/// section keyed by workflow id, e.g.:
/// <code>
/// IvrWorkflows:
///   banking-main: |
///     name: banking-main
///     stages: [...]
/// </code>
/// </summary>
public sealed class ConfigurationWorkflowSource : IIvrWorkflowDefinitionSource
{
    private readonly IConfiguration _section;

    public ConfigurationWorkflowSource(IConfiguration configuration, string sectionName = "IvrWorkflows")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(sectionName);
        _section = configuration.GetSection(sectionName);
    }

    public string Name { get; init; } = "configuration";

    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        var ids = _section.GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => c.Key)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<string>>(ids);
    }

    public ValueTask<IvrWorkflowSourceEntry?> LoadAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);
        var raw = _section[workflowId];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ValueTask.FromResult<IvrWorkflowSourceEntry?>(null);
        }

        return ValueTask.FromResult<IvrWorkflowSourceEntry?>(
            new IvrWorkflowSourceEntry(workflowId, raw, Name));
    }
}
