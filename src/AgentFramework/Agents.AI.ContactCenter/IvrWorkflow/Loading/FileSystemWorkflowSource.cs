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
/// <remarks>
/// Phase 2: filenames can carry an explicit integer version suffix using the
/// <c>name@N</c> convention (e.g. <c>subflows/verify@2.yaml</c> → id
/// <c>subflows.verify</c>, version <c>2</c>). When no suffix is present
/// <see cref="IvrWorkflowSourceEntry.Version"/> is reported as <see langword="null"/>
/// and the compiler falls back to the document's <c>version:</c> field (default
/// <c>1</c>). <see cref="ListAsync"/> still returns distinct ids; the
/// <see cref="IVersionedWorkflowSource"/> capability methods enumerate versions per
/// id and load a specific version on demand.
/// </remarks>
public sealed class FileSystemWorkflowSource : IIvrWorkflowDefinitionSource, IVersionedWorkflowSource
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

        var ids = EnumerateFiles()
            .Select(p => ParseFileName(p).Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<string>>(ids);
    }

    public ValueTask<IvrWorkflowSourceEntry?> LoadAsync(string workflowId, CancellationToken cancellationToken = default)
        => LoadAsync(workflowId, version: null, cancellationToken);

    public ValueTask<IReadOnlyList<int>> ListVersionsAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);
        if (!Directory.Exists(_rootDirectory))
        {
            return ValueTask.FromResult<IReadOnlyList<int>>([]);
        }

        var versions = EnumerateFiles()
            .Select(ParseFileName)
            .Where(parsed => parsed.Id.Equals(workflowId, StringComparison.OrdinalIgnoreCase))
            .Select(parsed => parsed.Version ?? 1)
            .Distinct()
            .OrderBy(v => v)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<int>>(versions);
    }

    public async ValueTask<IvrWorkflowSourceEntry?> LoadAsync(string workflowId, int? version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);
        if (!Directory.Exists(_rootDirectory))
        {
            return null;
        }

        var candidates = EnumerateFiles()
            .Select(path => (Path: path, Parsed: ParseFileName(path)))
            .Where(c => c.Parsed.Id.Equals(workflowId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        (string Path, FileNameParts Parsed) match;
        if (version is int requested)
        {
            // Versioned filenames win; an unversioned file participates as version 1.
            var picked = candidates
                .Where(c => (c.Parsed.Version ?? 1) == requested)
                .OrderByDescending(c => c.Parsed.Version.HasValue)
                .FirstOrDefault();
            if (picked.Path is null)
            {
                return null;
            }
            match = picked;
        }
        else
        {
            // Highest version. Tie-breaker: explicit @N filename wins over unversioned.
            match = candidates
                .OrderByDescending(c => c.Parsed.Version ?? 1)
                .ThenByDescending(c => c.Parsed.Version.HasValue)
                .First();
        }

        var yaml = await File.ReadAllTextAsync(match.Path, cancellationToken).ConfigureAwait(false);
        var fi = new FileInfo(match.Path);
        return new IvrWorkflowSourceEntry(
            workflowId,
            yaml,
            Name,
            ETag: null,
            LastModified: fi.LastWriteTimeUtc,
            Version: match.Parsed.Version);
    }

    private IEnumerable<string> EnumerateFiles() =>
        Directory.EnumerateFiles(_rootDirectory, "*.*", SearchOption.AllDirectories)
            .Where(p =>
            {
                var ext = Path.GetExtension(p);
                return ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".yml", StringComparison.OrdinalIgnoreCase);
            });

    /// <summary>
    /// Parse a file path into the workflow id and (optional) version suffix. Filenames
    /// of the form <c>name@N.yaml</c> map to <c>(parent.name, N)</c>; everything else
    /// maps to <c>(parent.name, null)</c>.
    /// </summary>
    private FileNameParts ParseFileName(string fullPath)
    {
        var relative = Path.GetRelativePath(_rootDirectory, fullPath);
        var noExt = Path.ChangeExtension(relative, null) ?? string.Empty;

        var lastSep = Math.Max(
            noExt.LastIndexOf(Path.DirectorySeparatorChar),
            noExt.LastIndexOf(Path.AltDirectorySeparatorChar));
        var parentSegment = lastSep >= 0 ? noExt[..lastSep] : string.Empty;
        var leaf = lastSep >= 0 ? noExt[(lastSep + 1)..] : noExt;

        int? version = null;
        var atIndex = leaf.LastIndexOf('@');
        if (atIndex > 0 && int.TryParse(leaf.AsSpan(atIndex + 1), out var parsed) && parsed >= 1)
        {
            version = parsed;
            leaf = leaf[..atIndex];
        }

        var pathPart = parentSegment.Length == 0
            ? leaf
            : parentSegment.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.') + "." + leaf;
        return new FileNameParts(pathPart, version);
    }

    private readonly record struct FileNameParts(string Id, int? Version);
}

