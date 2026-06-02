using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>
/// Runtime representation of one stage in a compiled call workflow. Pairs the authored
/// <see cref="StageBlueprint"/> with pre-compiled outgoing edges.
/// </summary>
public sealed class CompiledStage
{
    public CompiledStage(StageBlueprint blueprint, IReadOnlyList<CompiledStageEdge> outgoingEdges)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(outgoingEdges);

        Blueprint = blueprint;
        OutgoingEdges = outgoingEdges;
    }

    public string Id => Blueprint.Id;

    public bool Terminal => Blueprint.Terminal;

    public StageBlueprint Blueprint { get; }

    public IReadOnlyList<CompiledStageEdge> OutgoingEdges { get; }

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
