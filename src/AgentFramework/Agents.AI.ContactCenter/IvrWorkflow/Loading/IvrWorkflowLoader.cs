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

    /// <summary>Load and compile a workflow by id.</summary>
    ValueTask<CompiledIvrWorkflow> LoadAsync(string workflowId, CancellationToken cancellationToken = default);

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

    public async ValueTask<CompiledIvrWorkflow> LoadAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);
        var entry = await _source.LoadAsync(workflowId, cancellationToken).ConfigureAwait(false)
            ?? throw new IvrWorkflowYamlException(
                $"No workflow '{workflowId}' was found by source '{_source.Name}'.");

        return CompileEntry(entry);
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
        return _compiler.Compile(document, entry);
    }
}
