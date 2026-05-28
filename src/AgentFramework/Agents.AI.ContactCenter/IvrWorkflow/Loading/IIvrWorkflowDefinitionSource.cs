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
/// <param name="WorkflowId">Workflow id derived by the source (path / config key / blob name).</param>
/// <param name="Yaml">Raw YAML text.</param>
/// <param name="SourceName">Source identifier (for logging).</param>
/// <param name="ETag">Optional ETag for cache validation.</param>
/// <param name="LastModified">Optional last-modified stamp for cache validation.</param>
/// <param name="Version">
/// Phase 2: optional integer version surfaced by the source (e.g. parsed from a
/// <c>name@N.yaml</c> filename). When <see langword="null"/>, the compiler falls back
/// to the document's <c>version:</c> field (defaulting to <c>1</c>). Sources that
/// don't know how to enumerate versions return <see langword="null"/>.
/// </param>
public sealed record IvrWorkflowSourceEntry(
    string WorkflowId,
    string Yaml,
    string SourceName,
    string? ETag = null,
    DateTimeOffset? LastModified = null,
    int? Version = null);

/// <summary>
/// Optional capability interface a <see cref="IIvrWorkflowDefinitionSource"/> can
/// implement to expose multiple versions for the same workflow id (Phase 2). Sources
/// that don't implement this interface produce exactly one version per id, surfaced
/// through <see cref="IIvrWorkflowDefinitionSource.LoadAsync"/>.
/// </summary>
public interface IVersionedWorkflowSource : IIvrWorkflowDefinitionSource
{
    /// <summary>Enumerate the integer versions available for <paramref name="workflowId"/>, ascending.</summary>
    ValueTask<IReadOnlyList<int>> ListVersionsAsync(string workflowId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Load a specific version of <paramref name="workflowId"/>. <paramref name="version"/>
    /// of <see langword="null"/> resolves to the highest known version.
    /// </summary>
    ValueTask<IvrWorkflowSourceEntry?> LoadAsync(
        string workflowId,
        int? version,
        CancellationToken cancellationToken = default);
}
