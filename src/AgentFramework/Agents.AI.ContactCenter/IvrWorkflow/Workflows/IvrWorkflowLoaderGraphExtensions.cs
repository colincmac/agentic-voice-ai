using System;
using System.Threading;
using System.Threading.Tasks;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;
using Microsoft.Agents.AI.Workflows;

namespace Agents.AI.ContactCenter.IvrWorkflow.Workflows;

/// <summary>
/// Convenience extensions that load + compile + bridge a workflow in one call by combining
/// an <see cref="IIvrWorkflowLoader"/> with an <see cref="IIvrWorkflowGraphBuilder"/>.
/// </summary>
public static class IvrWorkflowLoaderGraphExtensions
{
    /// <summary>Load and compile <paramref name="workflowId"/>, then bridge the result into an Agent Framework <see cref="Workflow"/>.</summary>
    public static async ValueTask<Workflow> BuildGraphAsync(
        this IIvrWorkflowLoader loader,
        IIvrWorkflowGraphBuilder graphBuilder,
        string workflowId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(graphBuilder);
        ArgumentException.ThrowIfNullOrEmpty(workflowId);

        var compiled = await loader.LoadAsync(workflowId, cancellationToken).ConfigureAwait(false);
        return graphBuilder.Build(compiled);
    }
}
