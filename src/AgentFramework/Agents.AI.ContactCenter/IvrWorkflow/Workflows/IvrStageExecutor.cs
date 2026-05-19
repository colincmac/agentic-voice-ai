using System.Threading;
using System.Threading.Tasks;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Microsoft.Agents.AI.Workflows;

namespace Agents.AI.ContactCenter.IvrWorkflow.Workflows;

/// <summary>
/// Microsoft Agent Framework <see cref="Executor{T}"/> that represents a single compiled IVR
/// stage in the bridged workflow graph. The executor itself does not invoke the IVR runtime —
/// it carries the compiled stage payload for visualizers / orchestrators and forwards an
/// <see cref="IvrStageMessage"/> stamped with its own stage id so conditional edges
/// (registered by <see cref="IvrWorkflowGraphBuilder"/>) can route to the next stage.
/// </summary>
/// <remarks>
/// Terminal stages skip the forward step and yield their incoming message as a workflow
/// output via <see cref="IWorkflowContext.YieldOutputAsync(object, CancellationToken)"/>.
/// </remarks>
public sealed class IvrStageExecutor : Executor<IvrStageMessage>
{
    /// <summary>Gets the compiled stage payload this executor represents.</summary>
    public CompiledIvrStage Stage { get; }

    public IvrStageExecutor(CompiledIvrStage stage)
        : base(id: stage?.Id ?? throw new System.ArgumentNullException(nameof(stage)))
    {
        Stage = stage;
    }

    public override async ValueTask HandleAsync(
        IvrStageMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        // Re-stamp the message so downstream predicates can see which stage just ran.
        // Outgoing edges look at IvrStageMessage.FromStageId to gate themselves.
        var routed = message with { FromStageId = Stage.Id, StageId = Stage.Id };

        if (Stage.Terminal)
        {
            await context.YieldOutputAsync(routed, cancellationToken).ConfigureAwait(false);
            return;
        }

        await context.SendMessageAsync(routed, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
