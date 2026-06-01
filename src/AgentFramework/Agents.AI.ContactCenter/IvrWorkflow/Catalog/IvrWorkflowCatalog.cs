using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;

namespace Agents.AI.ContactCenter.IvrWorkflow.Catalog;

/// <summary>
/// Default <see cref="IIvrWorkflowCatalog"/>: lazily loads + compiles each requested
/// (workflow id, version) pair through the registered <see cref="IIvrWorkflowLoader"/>
/// and caches the result. Thread-safe; safe to register as a singleton.
/// </summary>
/// <remarks>
/// Phase 2: internal storage is <c>id → SortedDictionary&lt;int, CompiledIvrWorkflow&gt;</c>
/// so multiple versions per id coexist and version-pinned lookups can pick the highest
/// version satisfying a <c>[minVersion, maxVersion]</c> constraint. When the underlying
/// source doesn't track versions, every workflow registers under version <c>1</c>.
/// </remarks>
public sealed class IvrWorkflowCatalog : IIvrWorkflowCatalog
{
    private readonly IIvrWorkflowLoader _loader;
    private readonly ConcurrentDictionary<string, SortedDictionary<int, CompiledIvrWorkflow>> _byId
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _enumerationGate = new(1, 1);
    private bool _enumerated;

    public IvrWorkflowCatalog(IIvrWorkflowLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    public IReadOnlyCollection<string> Ids => [.. _byId.Keys];

    public IReadOnlyCollection<int> VersionsFor(string workflowId)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);
        if (!_byId.TryGetValue(workflowId, out var versions))
        {
            return [];
        }
        lock (versions)
        {
            return versions.Keys.ToArray();
        }
    }

    public bool TryGet(string workflowId, [NotNullWhen(true)] out CompiledIvrWorkflow? workflow)
        => TryGet(workflowId, minVersion: null, maxVersion: null, out workflow);

    public bool TryGet(
        string workflowId,
        int? minVersion,
        int? maxVersion,
        [NotNullWhen(true)] out CompiledIvrWorkflow? workflow)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);

        if (TryResolveFromCache(workflowId, minVersion, maxVersion, out workflow))
        {
            return true;
        }

        // Cache miss: ask the loader. When no version was pinned, load the latest from
        // the source (single Compile). When a specific version is requested, ask the
        // versioned loader overload directly.
        try
        {
            CompiledIvrWorkflow compiled;
            if (minVersion is null && maxVersion is null)
            {
                compiled = _loader.LoadAsync(workflowId).AsTask().GetAwaiter().GetResult();
            }
            else if (minVersion is int v && minVersion == maxVersion)
            {
                compiled = _loader.LoadAsync(workflowId, v).AsTask().GetAwaiter().GetResult();
            }
            else
            {
                // Range request: discover available versions then load matching ones so
                // the resolver below picks the highest in [min, max].
                var available = _loader.ListVersionsAsync(workflowId).AsTask().GetAwaiter().GetResult();
                foreach (var v2 in available.Where(v => InRange(v, minVersion, maxVersion)).OrderByDescending(v => v))
                {
                    var c = _loader.LoadAsync(workflowId, v2).AsTask().GetAwaiter().GetResult();
                    Cache(workflowId, c);
                }
                return TryResolveFromCache(workflowId, minVersion, maxVersion, out workflow);
            }

            workflow = Cache(workflowId, compiled);
            return InRange(workflow.Version <= 0 ? 1 : workflow.Version, minVersion, maxVersion);
        }
        catch (IvrWorkflowYamlException)
        {
            workflow = null;
            return false;
        }
    }

    public CompiledIvrWorkflow Get(string workflowId)
        => Get(workflowId, minVersion: null, maxVersion: null);

    public CompiledIvrWorkflow Get(string workflowId, int? minVersion, int? maxVersion)
    {
        if (!TryGet(workflowId, minVersion, maxVersion, out var workflow))
        {
            var bounds = (minVersion, maxVersion) switch
            {
                (null, null) => string.Empty,
                ({ } a, null) => $" matching version >= {a}",
                (null, { } b) => $" matching version <= {b}",
                ({ } a, { } b) when a == b => $" at version {a}",
                ({ } a, { } b) => $" matching version in [{a}, {b}]",
            };
            throw new KeyNotFoundException(
                $"Workflow '{workflowId}'{bounds} is not registered with any IVR workflow source.");
        }
        return workflow;
    }

    public async ValueTask EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_enumerated)
        {
            return;
        }
        await _enumerationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_enumerated)
            {
                return;
            }

            var ids = await _loader.ListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var id in ids)
            {
                var versions = await _loader.ListVersionsAsync(id, cancellationToken).ConfigureAwait(false);
                if (versions.Count == 0)
                {
                    // Source isn't version-aware — load the single available workflow.
                    try
                    {
                        var compiled = await _loader.LoadAsync(id, cancellationToken).ConfigureAwait(false);
                        Cache(id, compiled);
                    }
                    catch (IvrWorkflowYamlException) { }
                    continue;
                }

                foreach (var v in versions)
                {
                    try
                    {
                        var compiled = await _loader.LoadAsync(id, v, cancellationToken).ConfigureAwait(false);
                        Cache(id, compiled);
                    }
                    catch (IvrWorkflowYamlException) { }
                }
            }
            _enumerated = true;
        }
        finally
        {
            _enumerationGate.Release();
        }
    }

    private bool TryResolveFromCache(
        string workflowId,
        int? minVersion,
        int? maxVersion,
        [NotNullWhen(true)] out CompiledIvrWorkflow? workflow)
    {
        workflow = null;
        if (!_byId.TryGetValue(workflowId, out var versions))
        {
            return false;
        }

        lock (versions)
        {
            // SortedDictionary iterates ascending; reverse to get highest-first.
            foreach (var pair in versions.Reverse())
            {
                if (InRange(pair.Key, minVersion, maxVersion))
                {
                    workflow = pair.Value;
                    return true;
                }
            }
        }
        return false;
    }

    private CompiledIvrWorkflow Cache(string workflowId, CompiledIvrWorkflow compiled)
    {
        // Normalize to a positive integer; legacy CompiledIvrWorkflow.Version may be 0
        // when the YAML omitted the field and no filename version was supplied.
        var version = compiled.Version >= 1 ? compiled.Version : 1;
        var versions = _byId.GetOrAdd(workflowId, _ => new SortedDictionary<int, CompiledIvrWorkflow>());
        lock (versions)
        {
            // First write wins per version slot to avoid replacing an already-resolved
            // entry that callers may have cached.
            if (!versions.TryGetValue(version, out var existing))
            {
                versions[version] = compiled;
                return compiled;
            }
            return existing;
        }
    }

    private static bool InRange(int version, int? min, int? max)
        => (min is null || version >= min) && (max is null || version <= max);
}
