using YamlDotNet.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Definition;

/// <summary>
/// Body of a <c>type: subflow</c> stage. Specifies which child workflow the navigator
/// should push and (optionally) the parent-frame steps to resume on success / failure.
/// Both <see cref="OnSuccess"/> and <see cref="OnFailure"/> are also exposed as
/// shortcut fields directly on <see cref="IvrStageDocument"/> so authors can keep the
/// most common case readable:
/// <code>
/// - id: verify_caller
///   type: subflow
///   subflow: { workflowId: subflows.verify }
///   onSuccess: balance
///   onFailure: transfer
/// </code>
/// When both forms appear, the stage-level fields take precedence over those nested
/// inside the <c>subflow</c> block.
/// </summary>
public sealed class IvrSubflowReferenceDocument
{
    /// <summary>Id of the child workflow to push onto the frame stack. Required.</summary>
    [YamlMember(Alias = "workflowId")]
    public string WorkflowId { get; set; } = string.Empty;

    /// <summary>Parent-frame step id to enter after the child completes successfully.</summary>
    [YamlMember(Alias = "onSuccess")]
    public string? OnSuccess { get; set; }

    /// <summary>Parent-frame step id to enter after the child exits via a failure terminal stage.</summary>
    [YamlMember(Alias = "onFailure")]
    public string? OnFailure { get; set; }

    /// <summary>
    /// Phase 2: pin a lower-bound integer version for the referenced workflow. The
    /// catalog resolves to the highest version &gt;= <see cref="MinVersion"/> (and
    /// &lt;= <see cref="MaxVersion"/> when set). <see langword="null"/> means unbounded.
    /// </summary>
    [YamlMember(Alias = "minVersion")]
    public int? MinVersion { get; set; }

    /// <summary>Phase 2: pin an upper-bound integer version.</summary>
    [YamlMember(Alias = "maxVersion")]
    public int? MaxVersion { get; set; }
}
