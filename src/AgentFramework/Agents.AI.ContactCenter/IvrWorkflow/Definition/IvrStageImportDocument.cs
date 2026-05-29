using YamlDotNet.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Definition;

/// <summary>
/// Body of a stage entry that imports its content from another workflow's stage at
/// compile time. Unlike <c>type: subflow</c> (Phase 1), an import does NOT push a new
/// frame at runtime — the referenced stage is cloned and inlined into the parent
/// workflow's step list under the local alias, so it behaves indistinguishably from a
/// stage authored inline.
/// <code>
/// stages:
///   - import:
///       stage: subflows.closing       # "&lt;workflowId&gt;.&lt;stageId&gt;" reference
///       as: closing                   # optional local stage id; defaults to source stage id
///       minVersion: 1
///       maxVersion: 2
/// </code>
/// Phase 2 only supports importing **leaf** stages (no outbound transitions). Stages
/// with transitions are rejected with a compile error suggesting <c>type: subflow</c>
/// instead — rewriting transition targets across workflows is ambiguous and out of
/// scope.
/// </summary>
public sealed class IvrStageImportDocument
{
    /// <summary>
    /// Reference to the source stage. Required. Resolved against the
    /// <see cref="Catalog.IIvrWorkflowCatalog"/> using a catalog-aware longest-prefix
    /// match, so two forms are accepted:
    /// <list type="number">
    ///   <item>
    ///     <description>
    ///       <b>Bare workflow id</b> — when the referenced workflow has exactly one
    ///       stage, that stage is imported (e.g. <c>stage: subflows.closing</c> where
    ///       <c>subflows.closing</c> is itself a single-stage workflow). Importing a
    ///       bare id whose workflow has 2+ stages is a compile error and the message
    ///       prompts for the explicit <c>workflowId.stageId</c> form.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <b><c>workflowId.stageId</c></b> — multi-segment workflow ids are
    ///       supported via right-to-left longest-prefix lookup (e.g. <c>banking.lib.closing</c>
    ///       resolves to workflow <c>banking.lib</c>, stage <c>closing</c> when both
    ///       <c>banking</c> and <c>banking.lib</c> are registered; the longer/more
    ///       specific prefix wins).
    ///     </description>
    ///   </item>
    /// </list>
    /// <see cref="MinVersion"/> / <see cref="MaxVersion"/> participate in resolution:
    /// a workflow id only matches when the catalog has a version satisfying the pin
    /// constraints, so the same reference can resolve differently across version
    /// windows.
    /// </summary>
    [YamlMember(Alias = "stage")]
    public string Stage { get; set; } = string.Empty;

    /// <summary>
    /// Optional local stage id the imported stage should be exposed under. When omitted
    /// the local id matches the source stage id.
    /// </summary>
    [YamlMember(Alias = "as")]
    public string? As { get; set; }

    /// <summary>Optional lower-bound version constraint on the source workflow.</summary>
    [YamlMember(Alias = "minVersion")]
    public int? MinVersion { get; set; }

    /// <summary>Optional upper-bound version constraint on the source workflow.</summary>
    [YamlMember(Alias = "maxVersion")]
    public int? MaxVersion { get; set; }
}
