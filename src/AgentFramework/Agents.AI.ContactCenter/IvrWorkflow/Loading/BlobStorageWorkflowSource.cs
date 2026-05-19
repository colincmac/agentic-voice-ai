using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Agents.AI.ContactCenter.IvrWorkflow.Loading;

/// <summary>
/// Source that reads workflow YAML documents from a single Azure Blob Storage container.
/// Each blob is one workflow; the workflow id is derived from the blob name minus the
/// <c>.yaml</c>/<c>.yml</c> extension.
/// </summary>
public sealed class BlobStorageWorkflowSource : IIvrWorkflowDefinitionSource
{
    private readonly BlobContainerClient _container;

    public BlobStorageWorkflowSource(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);
        _container = container;
    }

    public string Name { get; init; } = "azure-blob";

    public async ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        await foreach (BlobItem blob in _container.GetBlobsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (!IsYamlBlob(blob.Name))
            {
                continue;
            }
            ids.Add(WorkflowIdFromBlobName(blob.Name));
        }
        return ids;
    }

    public async ValueTask<IvrWorkflowSourceEntry?> LoadAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);

        BlobClient? blobClient = null;
        await foreach (BlobItem blob in _container.GetBlobsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (!IsYamlBlob(blob.Name))
            {
                continue;
            }
            if (WorkflowIdFromBlobName(blob.Name).Equals(workflowId, StringComparison.OrdinalIgnoreCase))
            {
                blobClient = _container.GetBlobClient(blob.Name);
                break;
            }
        }

        if (blobClient is null)
        {
            return null;
        }

        try
        {
            Response<BlobDownloadResult> response =
                await blobClient.DownloadContentAsync(cancellationToken).ConfigureAwait(false);

            var yaml = response.Value.Content.ToString();
            var props = response.Value.Details;
            return new IvrWorkflowSourceEntry(
                workflowId,
                yaml,
                Name,
                ETag: props.ETag.ToString(),
                LastModified: props.LastModified);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private static bool IsYamlBlob(string name)
    {
        var ext = Path.GetExtension(name);
        return ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".yml", StringComparison.OrdinalIgnoreCase);
    }

    private static string WorkflowIdFromBlobName(string name)
    {
        var noExt = Path.ChangeExtension(name, null);
        return noExt!.Replace('/', '.');
    }
}
