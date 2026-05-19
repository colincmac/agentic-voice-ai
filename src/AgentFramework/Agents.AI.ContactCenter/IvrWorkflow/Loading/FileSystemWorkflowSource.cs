using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Agents.AI.ContactCenter.IvrWorkflow.Loading;

/// <summary>
/// File-system source that reads <c>*.yaml</c> / <c>*.yml</c> files from a directory tree.
/// The workflow id is derived from the relative path with separators replaced by <c>.</c>
/// and the extension dropped — e.g. <c>banking/greeting.yaml</c> -> <c>banking.greeting</c>.
/// </summary>
public sealed class FileSystemWorkflowSource : IIvrWorkflowDefinitionSource
{
    private readonly string _rootDirectory;

    public FileSystemWorkflowSource(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string Name { get; init; } = "filesystem";

    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_rootDirectory))
        {
            return ValueTask.FromResult<IReadOnlyList<string>>([]);
        }

        var ids = EnumerateFiles().Select(WorkflowIdFromPath).Distinct(StringComparer.Ordinal).ToArray();
        return ValueTask.FromResult<IReadOnlyList<string>>(ids);
    }

    public async ValueTask<IvrWorkflowSourceEntry?> LoadAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);
        if (!Directory.Exists(_rootDirectory))
        {
            return null;
        }

        var match = EnumerateFiles().FirstOrDefault(p => WorkflowIdFromPath(p).Equals(workflowId, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return null;
        }

        var yaml = await File.ReadAllTextAsync(match, cancellationToken).ConfigureAwait(false);
        var fi = new FileInfo(match);
        return new IvrWorkflowSourceEntry(
            workflowId,
            yaml,
            Name,
            ETag: null,
            LastModified: fi.LastWriteTimeUtc);
    }

    private IEnumerable<string> EnumerateFiles() =>
        Directory.EnumerateFiles(_rootDirectory, "*.*", SearchOption.AllDirectories)
            .Where(p =>
            {
                var ext = Path.GetExtension(p);
                return ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".yml", StringComparison.OrdinalIgnoreCase);
            });

    private string WorkflowIdFromPath(string fullPath)
    {
        var relative = Path.GetRelativePath(_rootDirectory, fullPath);
        var noExt = Path.ChangeExtension(relative, null);
        return noExt!.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.');
    }
}
