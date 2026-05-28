using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Microsoft.Agents.AI.Workflows;

namespace Agents.AI.ContactCenter.IvrWorkflow.Workflows;

/// <summary>
/// Translates a <see cref="CompiledIvrWorkflow"/> into a Microsoft Agent Framework
/// <see cref="Workflow"/> graph. Each YAML stage becomes one <see cref="IvrStageExecutor"/>
/// node, and the aggregated stage transitions (from explicit YAML <c>transitions:</c>,
/// intent <c>next_stage</c>, DTMF <c>next_stage</c>, and <c>on_exit</c>) become conditional
/// edges between executors. Terminal stages yield output instead of fanning out.
/// </summary>
/// <remarks>
/// <para>
/// The default <see cref="Build(CompiledIvrWorkflow)"/> overload produces a
/// <em>preview projection</em> of the declarative IVR: it does not invoke the runtime by
/// itself. Use it for visualization (<see cref="WorkflowVisualizer"/>), structured
/// composition with other Agent Framework executors, or graph-shape tests.
/// </para>
/// <para>
/// The <see cref="Build(CompiledIvrWorkflow, IvrStageRunnerSelector)"/> overload binds each
/// generated <see cref="IvrStageExecutor"/> to a per-call <see cref="IIvrStageRunner"/> so
/// the workflow becomes the live orchestrator: stage entry pushes the prompt + tool surface
/// to the strategy, and the runner's <see cref="IvrStageOutcome"/> drives the graph
/// transition through <see cref="IvrStageMessage.NextStageIdHint"/>.
/// </para>
/// </remarks>
public interface IIvrWorkflowGraphBuilder
{
    /// <summary>
    /// Builds a preview-mode workflow graph for the given compiled IVR workflow. Equivalent
    /// to <see cref="Build(CompiledIvrWorkflow, IvrStageRunnerSelector)"/> with a selector
    /// that returns <see langword="null"/> for every stage.
    /// </summary>
    Workflow Build(CompiledIvrWorkflow workflow);

    /// <summary>
    /// Builds a live-mode workflow graph. Each generated <see cref="IvrStageExecutor"/> is
    /// bound to the runner returned by <paramref name="runnerSelector"/>; stages for which
    /// the selector returns <see langword="null"/> remain in preview mode (useful when the
    /// strategy only owns a subset of stages).
    /// </summary>
    /// <param name="workflow">Compiled IVR workflow to lower into a graph.</param>
    /// <param name="runnerSelector">Per-stage runner lookup; never invoked with a null stage.</param>
    Workflow Build(CompiledIvrWorkflow workflow, IvrStageRunnerSelector runnerSelector);
}

/// <summary>Thrown when a <see cref="CompiledIvrWorkflow"/> cannot be lowered into a graph.</summary>
public sealed class IvrWorkflowGraphBuildException : Exception
{
    public IvrWorkflowGraphBuildException(string workflow, string error)
        : base($"Failed to build Agent Framework workflow graph for IVR workflow '{workflow}': {error}")
    {
        Workflow = workflow;
    }

    public string Workflow { get; }
}
