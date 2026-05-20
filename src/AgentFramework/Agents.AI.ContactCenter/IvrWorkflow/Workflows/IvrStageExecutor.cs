using System;
using System.Threading;
using System.Threading.Tasks;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Microsoft.Agents.AI.Workflows;

namespace Agents.AI.ContactCenter.IvrWorkflow.Workflows;

/// <summary>
/// Microsoft Agent Framework <see cref="Executor{T}"/> that represents a single compiled IVR
/// stage in the bridged workflow graph. The executor operates in one of two modes depending
/// on how it was constructed:
/// </summary>
/// <remarks>
/// <para>
/// <b>Live mode</b> — when constructed with an <see cref="IIvrStageRunner"/>, the executor
/// awaits the runner to apply the stage to the live conversation strategy and translate the
/// resulting <see cref="IvrStageOutcome"/> into a graph transition
/// (<see cref="IvrStageMessage.NextStageIdHint"/>), a workflow output (terminal /
/// <see cref="IvrStageOutcomeKind.Complete"/>), a re-entry to the same stage
/// (<see cref="IvrStageOutcomeKind.Retry"/>, after a composite tier swap), or a fault output
/// (<see cref="IvrStageFaultedOutput"/>). In this mode the graph itself sequences the call.
/// </para>
/// <para>
/// <b>Preview mode</b> — when constructed without a runner (or with a runner that is
/// <see langword="null"/>), the executor behaves as a graph projection of the IVR: it
/// re-stamps the incoming <see cref="IvrStageMessage"/> with its own stage id so conditional
/// edges (registered by <see cref="IvrWorkflowGraphBuilder"/>) can route to the next stage,
/// or yields output for terminal stages. The live call runtime in
/// <see cref="Calling.Core.CallSessionFactory"/> currently consumes
/// <see cref="CompiledIvrWorkflow.Runtime"/> directly; preview mode keeps the visualization
/// and graph-shape tests working unchanged until <c>CallSession</c> is flipped to drive the
/// graph (planned follow-up).
/// </para>
/// <para>
/// Terminal stages skip the forward step and yield their incoming message as a workflow
/// output via <see cref="IWorkflowContext.YieldOutputAsync(object, CancellationToken)"/>.
/// </para>
/// </remarks>
public sealed class IvrStageExecutor : Executor<IvrStageMessage>
{
    private readonly IIvrStageRunner? _runner;

    /// <summary>Gets the compiled stage payload this executor represents.</summary>
    public CompiledIvrStage Stage { get; }

    /// <summary>
    /// Gets the runner this executor will drive on <see cref="HandleAsync"/>, or
    /// <see langword="null"/> when the executor was constructed in preview mode.
    /// </summary>
    public IIvrStageRunner? Runner => _runner;

    /// <summary>Preview-mode constructor.</summary>
    public IvrStageExecutor(CompiledIvrStage stage)
        : this(stage, runner: null)
    {
    }

    /// <summary>Live-mode constructor; <paramref name="runner"/> may be <see langword="null"/> to fall back to preview mode.</summary>
    public IvrStageExecutor(CompiledIvrStage stage, IIvrStageRunner? runner)
        : base(id: stage?.Id ?? throw new ArgumentNullException(nameof(stage)))
    {
        Stage = stage;
        _runner = runner;
    }

    /// <summary>
    /// Declare the outputs this executor can yield so the workflow framework's
    /// <see cref="IWorkflowContext.YieldOutputAsync(object, CancellationToken)"/> route
    /// accepts them. Both preview and live modes can yield an <see cref="IvrStageMessage"/>
    /// (terminal stage / <see cref="IvrStageOutcomeKind.Complete"/>) and live mode may also
    /// yield an <see cref="IvrStageFaultedOutput"/>.
    /// </summary>
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder builder) =>
        base.ConfigureProtocol(builder)
            .SendsMessage<IvrStageMessage>()
            .YieldsOutput<IvrStageMessage>()
            .YieldsOutput<IvrStageFaultedOutput>();

    public override async ValueTask HandleAsync(
        IvrStageMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        if (_runner is null)
        {
            await HandlePreviewAsync(message, context, cancellationToken).ConfigureAwait(false);
            return;
        }

        await HandleLiveAsync(message, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Preview behavior: re-stamp and forward (or yield, for terminal stages). No strategy
    /// invocation. Used by visualization and graph-shape tests.
    /// </summary>
    private async ValueTask HandlePreviewAsync(
        IvrStageMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        // Outgoing edges look at IvrStageMessage.FromStageId to gate themselves.
        var routed = message with { FromStageId = Stage.Id, StageId = Stage.Id };

        if (Stage.Terminal)
        {
            await context.YieldOutputAsync(routed, cancellationToken).ConfigureAwait(false);
            return;
        }

        await context.SendMessageAsync(routed, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Live behavior: invoke the runner, translate the outcome into a graph action. Terminal
    /// stages still yield output; non-terminal stages forward an <see cref="IvrStageMessage"/>
    /// whose <see cref="IvrStageMessage.NextStageIdHint"/> drives the existing conditional
    /// edges in <see cref="IvrWorkflowGraphBuilder"/>.
    /// </summary>
    private async ValueTask HandleLiveAsync(
        IvrStageMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        // Stamp provenance so downstream predicates can see which stage just ran, regardless
        // of which branch we end up taking.
        var stamped = message with { FromStageId = Stage.Id, StageId = Stage.Id };

        IvrStageOutcome outcome;
        try
        {
            outcome = await _runner!.EnterStageAsync(Stage, stamped, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Workflow / call shutting down — nothing more to do.
            return;
        }
        catch (Exception ex)
        {
            await context.YieldOutputAsync(
                new IvrStageFaultedOutput(Stage.Id, ex.Message, ex),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (outcome.Kind)
        {
            case IvrStageOutcomeKind.Transition:
                {
                    if (Stage.Terminal)
                    {
                        // A terminal stage can't transition. Treat as Complete instead so the
                        // graph host doesn't sit waiting for an edge that doesn't exist.
                        await context.YieldOutputAsync(
                            stamped with { State = outcome.State ?? stamped.State },
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    var forwarded = stamped with
                    {
                        NextStageIdHint = outcome.NextStageId,
                        State = outcome.State ?? stamped.State,
                    };
                    await context.SendMessageAsync(forwarded, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return;
                }

            case IvrStageOutcomeKind.Retry:
                {
                    // Re-enter the same stage. Composite uses this after swapping to a
                    // lower-tier strategy without advancing the navigator.
                    var retried = stamped with { NextStageIdHint = Stage.Id };
                    await context.SendMessageAsync(retried, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return;
                }

            case IvrStageOutcomeKind.Complete:
                {
                    await context.YieldOutputAsync(
                        stamped with { State = outcome.State ?? stamped.State },
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

            case IvrStageOutcomeKind.Faulted:
                {
                    await context.YieldOutputAsync(
                        new IvrStageFaultedOutput(
                            Stage.Id,
                            outcome.Reason ?? "stage runner reported a fault",
                            outcome.Exception),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

            default:
                throw new InvalidOperationException(
                    $"Unknown IvrStageOutcomeKind '{outcome.Kind}' returned by runner for stage '{Stage.Id}'.");
        }
    }
}
