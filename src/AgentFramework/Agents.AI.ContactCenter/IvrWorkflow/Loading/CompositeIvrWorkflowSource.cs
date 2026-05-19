using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Agents.AI.ContactCenter.IvrWorkflow.Loading;

/// <summary>
/// Composite source that walks an ordered list of inner sources and returns the first
/// matching workflow. <see cref="ListAsync"/> de-duplicates ids across sources while
/// preserving order (first occurrence wins).
/// </summary>
public sealed class CompositeIvrWorkflowSource : IIvrWorkflowDefinitionSource
{
    private readonly IReadOnlyList<IIvrWorkflowDefinitionSource> _sources;

    public CompositeIvrWorkflowSource(IEnumerable<IIvrWorkflowDefinitionSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToArray();
    }

    public string Name => "composite";

    public async ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new List<string>();
        foreach (var source in _sources)
        {
            foreach (var id in await source.ListAsync(cancellationToken).ConfigureAwait(false))
            {
                if (seen.Add(id))
                {
                    ids.Add(id);
                }
            }
        }
        return ids;
    }

    public async ValueTask<IvrWorkflowSourceEntry?> LoadAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        foreach (var source in _sources)
        {
            var entry = await source.LoadAsync(workflowId, cancellationToken).ConfigureAwait(false);
            if (entry is not null)
            {
                return entry;
            }
        }
        return null;
    }
}
