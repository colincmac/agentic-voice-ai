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
/// The produced workflow is a graph projection of the declarative IVR; it does not invoke
/// the realtime/DTMF runtime by itself. Use it for visualization
/// (<see cref="WorkflowVisualizer"/>), structured orchestration, or composition with other
/// Agent Framework executors.
/// </remarks>
public interface IIvrWorkflowGraphBuilder
{
    /// <summary>Builds the workflow graph for the given compiled IVR workflow.</summary>
    Workflow Build(CompiledIvrWorkflow workflow);
}

/// <summary>Thrown when a <see cref="CompiledIvrWorkflow"/> cannot be lowered into a graph.</summary>
public sealed class IvrWorkflowGraphBuildException : System.Exception
{
    public IvrWorkflowGraphBuildException(string workflow, string error)
        : base($"Failed to build Agent Framework workflow graph for IVR workflow '{workflow}': {error}")
    {
        Workflow = workflow;
    }

    public string Workflow { get; }
}
