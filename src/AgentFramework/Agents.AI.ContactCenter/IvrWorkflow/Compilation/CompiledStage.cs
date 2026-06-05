using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Tools;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>
/// Runtime representation of one stage in a compiled call workflow. Pairs the authored
/// <see cref="StageBlueprint"/> with pre-compiled outgoing edges and the resolved
/// tool surface.
/// </summary>
public sealed class CompiledStage
{
    /// <summary>Back-compat ctor — equivalent to passing an empty <see cref="ToolBindings"/> list. Preserved for test fixtures that build stages directly.</summary>
    public CompiledStage(StageBlueprint blueprint, IReadOnlyList<CompiledStageEdge> outgoingEdges)
        : this(blueprint, outgoingEdges, toolBindings: [])
    {
    }

    public CompiledStage(
        StageBlueprint blueprint,
        IReadOnlyList<CompiledStageEdge> outgoingEdges,
        IReadOnlyList<ToolBinding> toolBindings)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(outgoingEdges);
        ArgumentNullException.ThrowIfNull(toolBindings);

        Blueprint = blueprint;
        OutgoingEdges = outgoingEdges;
        ToolBindings = toolBindings;
    }

    public string Id => Blueprint.Id;

    public bool Terminal => Blueprint.Terminal;

    public StageBlueprint Blueprint { get; }

    public IReadOnlyList<CompiledStageEdge> OutgoingEdges { get; }

    /// <summary>
    /// Tool bindings resolved from the workflow's <see cref="WorkflowBlueprint.CommonToolNames"/>,
    /// this stage's <see cref="StageBlueprint.ToolNames"/>, and the stage's
    /// <see cref="StageRealtimePrompt.ToolNames"/>, deduped in author order (last-wins on
    /// name collision). Populated by <see cref="WorkflowGraphCompiler"/> when a
    /// <see cref="Tools.IIvrToolRegistry"/> is supplied; empty when no registry is provided.
    /// </summary>
    public IReadOnlyList<ToolBinding> ToolBindings { get; }

    /// <summary>Find the outgoing edge whose <see cref="CompiledStageEdge.TargetStageId"/> matches; <see langword="null"/> if none.</summary>
    public CompiledStageEdge? FindEdgeTo(string targetStageId)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetStageId);
        for (var i = 0; i < OutgoingEdges.Count; i++)
        {
            if (string.Equals(OutgoingEdges[i].TargetStageId, targetStageId, StringComparison.Ordinal))
            {
                return OutgoingEdges[i];
            }
        }
        return null;
    }

    /// <summary>Find the outgoing edge by transition label (case-insensitive).</summary>
    public CompiledStageEdge? FindEdgeByLabel(string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        for (var i = 0; i < OutgoingEdges.Count; i++)
        {
            if (string.Equals(OutgoingEdges[i].Label, label, StringComparison.OrdinalIgnoreCase))
            {
                return OutgoingEdges[i];
            }
        }
        return null;
    }
}
