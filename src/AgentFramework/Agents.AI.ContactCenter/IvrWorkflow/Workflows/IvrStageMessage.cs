using System.Collections.Generic;

namespace Agents.AI.ContactCenter.IvrWorkflow.Workflows;

/// <summary>
/// Message dispatched between <see cref="IvrStageExecutor"/> nodes in a graph produced by
/// <see cref="IIvrWorkflowGraphBuilder"/>. Carries the active stage id, the most recent
/// provenance hop, an optional explicit next-stage hint (set by tools/intents/DTMF when
/// they already know where to route), and a property bag for collected caller state.
/// </summary>
/// <param name="StageId">The id of the stage that just executed, or the entry stage on workflow start.</param>
/// <param name="FromStageId">The id of the previously-executed stage, or <see langword="null"/> at workflow entry.</param>
/// <param name="NextStageIdHint">
/// Optional explicit hint for the next stage. When set, conditional edges built by
/// <see cref="IvrWorkflowGraphBuilder"/> will route to the matching outgoing edge even when
/// other predicates could fire.
/// </param>
/// <param name="State">
/// Snapshot of collected caller state (intent values, verification claims, menu choices).
/// The bridge treats this as an opaque dictionary; the IVR runtime owns its actual lifetime.
/// </param>
public sealed record IvrStageMessage(
    string StageId,
    string? FromStageId = null,
    string? NextStageIdHint = null,
    IReadOnlyDictionary<string, object?>? State = null)
{
    /// <summary>Returns a copy with <see cref="FromStageId"/> set to <paramref name="from"/> and <see cref="StageId"/> set to <paramref name="to"/>.</summary>
    public IvrStageMessage RouteTo(string to, string from) =>
        this with { StageId = to, FromStageId = from, NextStageIdHint = null };
}
