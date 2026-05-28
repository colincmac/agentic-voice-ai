using System.Diagnostics.CodeAnalysis;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;

namespace Agents.AI.ContactCenter.IvrWorkflow.Catalog;

/// <summary>
/// Per-process registry of compiled IVR workflows resolved by id. Backed by
/// <see cref="Loading.IIvrWorkflowLoader"/>; populated lazily on first read and cached
/// thereafter (compiled workflows are immutable). Introduced by Phase 1 of the
/// composable-workflow refactor so a <see cref="WorkflowFrame"/> can identify the
/// workflow it belongs to and the navigator can resolve steps against any active
/// frame's workflow rather than a single bound <see cref="RealtimeIvrWorkflowDefinition"/>.
/// </summary>
/// <remarks>
/// Phase 1 ignores the <c>version</c> dimension entirely (one workflow per id, latest wins).
/// Phase 2 will widen the surface to <c>TryGet(id, versionSpec, out workflow)</c> and resolve
/// the highest <see cref="CompiledIvrWorkflow.Version"/> satisfying the request.
/// </remarks>
public interface IIvrWorkflowCatalog
{
    /// <summary>
    /// Resolve the latest compiled workflow with this id. Returns <see langword="false"/>
    /// when no source produces a workflow with that id (e.g. typo in a
    /// <c>subflow.workflowId</c>). Back-compat shim — equivalent to
    /// <see cref="TryGet(string, int?, int?, out CompiledIvrWorkflow?)"/> with no bounds.
    /// </summary>
    bool TryGet(string workflowId, [NotNullWhen(true)] out CompiledIvrWorkflow? workflow);

    /// <summary>
    /// Phase 2: resolve the highest known version of <paramref name="workflowId"/>
    /// satisfying <c>[minVersion, maxVersion]</c>. Both bounds are inclusive; either
    /// may be <see langword="null"/> to mean unbounded. Returns <see langword="false"/>
    /// when no version satisfies the constraint (or no such workflow exists).
    /// </summary>
    bool TryGet(
        string workflowId,
        int? minVersion,
        int? maxVersion,
        [NotNullWhen(true)] out CompiledIvrWorkflow? workflow);

    /// <summary>Convenience over <see cref="TryGet(string, out CompiledIvrWorkflow?)"/> that throws when the id is unknown.</summary>
    CompiledIvrWorkflow Get(string workflowId);

    /// <summary>Convenience over the version-pinned <see cref="TryGet(string, int?, int?, out CompiledIvrWorkflow?)"/> that throws when no version matches.</summary>
    CompiledIvrWorkflow Get(string workflowId, int? minVersion, int? maxVersion);

    /// <summary>Integer versions currently cached for <paramref name="workflowId"/>, ascending. Useful for diagnostics.</summary>
    IReadOnlyCollection<int> VersionsFor(string workflowId);

    /// <summary>All workflow ids the catalog has been asked to load so far, or every known id once <see cref="EnsureLoadedAsync"/> has run.</summary>
    IReadOnlyCollection<string> Ids { get; }

    /// <summary>
    /// Force the catalog to enumerate the underlying source(s) and compile every known
    /// (id, version) pair. Idempotent; safe to call from startup-time warm-up paths.
    /// </summary>
    ValueTask EnsureLoadedAsync(CancellationToken cancellationToken = default);
}
