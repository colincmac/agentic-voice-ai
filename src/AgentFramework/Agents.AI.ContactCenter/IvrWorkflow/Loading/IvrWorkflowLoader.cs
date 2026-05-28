using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Definition;

namespace Agents.AI.ContactCenter.IvrWorkflow.Loading;

/// <summary>
/// High-level loader that combines an <see cref="IIvrWorkflowDefinitionSource"/> with
/// the YAML reader, document validator, and an <see cref="IIvrWorkflowCompiler"/> to
/// produce a runtime-ready <see cref="CompiledIvrWorkflow"/>.
/// </summary>
public interface IIvrWorkflowLoader
{
    /// <summary>Enumerate available workflow ids across the configured source(s).</summary>
    ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Load and compile a workflow by id (latest version when the source carries multiple).</summary>
    ValueTask<CompiledIvrWorkflow> LoadAsync(string workflowId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase 2: load and compile a specific version. <paramref name="version"/> of
    /// <see langword="null"/> resolves to the highest version known to the source.
    /// Sources that don't implement <see cref="IVersionedWorkflowSource"/> ignore the
    /// argument and return their single available workflow.
    /// </summary>
    ValueTask<CompiledIvrWorkflow> LoadAsync(string workflowId, int? version, CancellationToken cancellationToken = default);

    /// <summary>Enumerate the integer versions a source can produce for a given id. Empty for sources that don't track versions.</summary>
    ValueTask<IReadOnlyList<int>> ListVersionsAsync(string workflowId, CancellationToken cancellationToken = default);

    /// <summary>Compile a YAML string directly without going through a source (test/dev helper).</summary>
    CompiledIvrWorkflow Compile(string yaml, string? sourceName = null);
}

/// <inheritdoc cref="IIvrWorkflowLoader"/>
public sealed class IvrWorkflowLoader : IIvrWorkflowLoader
{
    private readonly IIvrWorkflowDefinitionSource _source;
    private readonly IIvrWorkflowCompiler _compiler;

    public IvrWorkflowLoader(IIvrWorkflowDefinitionSource source, IIvrWorkflowCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(compiler);
        _source = source;
        _compiler = compiler;
    }

    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) =>
        _source.ListAsync(cancellationToken);

    public ValueTask<CompiledIvrWorkflow> LoadAsync(string workflowId, CancellationToken cancellationToken = default)
        => LoadAsync(workflowId, version: null, cancellationToken);

    public async ValueTask<CompiledIvrWorkflow> LoadAsync(string workflowId, int? version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);

        var entry = _source is IVersionedWorkflowSource versioned
            ? await versioned.LoadAsync(workflowId, version, cancellationToken).ConfigureAwait(false)
            : await _source.LoadAsync(workflowId, cancellationToken).ConfigureAwait(false);

        if (entry is null)
        {
            var versionPart = version is int v ? $" (version {v})" : string.Empty;
            throw new IvrWorkflowYamlException(
                $"No workflow '{workflowId}'{versionPart} was found by source '{_source.Name}'.");
        }

        return CompileEntry(entry);
    }

    public async ValueTask<IReadOnlyList<int>> ListVersionsAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);
        if (_source is IVersionedWorkflowSource versioned)
        {
            return await versioned.ListVersionsAsync(workflowId, cancellationToken).ConfigureAwait(false);
        }
        return [];
    }

    public CompiledIvrWorkflow Compile(string yaml, string? sourceName = null)
    {
        var document = IvrWorkflowYamlReader.Parse(yaml, sourceName);
        IvrWorkflowDocumentValidator.Validate(document).ThrowIfInvalid(document.Name);
        return _compiler.Compile(document);
    }

    private CompiledIvrWorkflow CompileEntry(IvrWorkflowSourceEntry entry)
    {
        var document = IvrWorkflowYamlReader.Parse(entry.Yaml, entry.SourceName);
        IvrWorkflowDocumentValidator.Validate(document).ThrowIfInvalid(document.Name);

        // Filename-derived version overrides the document's version: field (which itself
        // defaults to 1). Applied here so the compiler/CompiledIvrWorkflow.Version sees
        // the same value regardless of whether the YAML carried an explicit `version:`.
        if (entry.Version is int v && v >= 1)
        {
            document.Version = v;
        }

        return _compiler.Compile(document, entry);
    }
}
