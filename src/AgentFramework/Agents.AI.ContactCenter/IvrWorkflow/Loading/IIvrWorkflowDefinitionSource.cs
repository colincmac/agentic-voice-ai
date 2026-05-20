using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Agents.AI.ContactCenter.IvrWorkflow.Loading;

/// <summary>
/// Pluggable source of IVR workflow YAML documents. Implementations are responsible
/// for locating raw YAML by workflow id and returning the text alongside metadata
/// (used for caching / change detection by <see cref="IIvrWorkflowLoader"/>).
/// </summary>
public interface IIvrWorkflowDefinitionSource
{
    /// <summary>Source identifier (for logging and ordering in <see cref="CompositeIvrWorkflowSource"/>).</summary>
    string Name { get; }

    /// <summary>
    /// Enumerate the workflow ids this source can produce. May return an empty sequence
    /// when the source does not support enumeration (e.g., a remote source where listing
    /// is expensive).
    /// </summary>
    ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Load a single workflow by id. Returns <see langword="null"/> when the source has no document for that id.</summary>
    ValueTask<IvrWorkflowSourceEntry?> LoadAsync(string workflowId, CancellationToken cancellationToken = default);
}

/// <summary>Raw YAML payload + provenance metadata returned by an <see cref="IIvrWorkflowDefinitionSource"/>.</summary>
public sealed record IvrWorkflowSourceEntry(
    string WorkflowId,
    string Yaml,
    string SourceName,
    string? ETag = null,
    DateTimeOffset? LastModified = null);
