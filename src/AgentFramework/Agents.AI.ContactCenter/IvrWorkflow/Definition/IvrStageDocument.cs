using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Definition;

/// <summary>
/// A workflow stage. Stages own conversation state, declare locally-scoped intents,
/// and reference shared capabilities. A stage is executed by exactly one of two
/// configuration categories at runtime, selected by the active <see cref="IvrStrategyDocument"/>
/// and any composite fallback policy:
/// <list type="bullet">
///   <item><b>Realtime</b> (generative AI / LLM voice agent) — configured via <see cref="Realtime"/>.</item>
///   <item><b>Scripted</b> (non-generative DTMF menus and NLU intent recognition) — configured via
///   <see cref="Scripted"/>. The DTMF and NLU tiers share most of their prompt surface and
///   control knobs at the <c>scripted:</c> root, with thin per-tier override blocks
///   (<c>scripted.nlu</c> / <c>scripted.dtmf</c>) for the values that genuinely differ.</item>
/// </list>
/// A stage may declare any combination of these blocks; the strategy selector picks
/// the highest-priority tier whose configuration is present, then falls back to the next
/// tier per the workflow's strategy. Stage-level <c>intents</c> are shared across the NLU
/// and Realtime tiers (DTMF uses its own option list under <see cref="Scripted"/>).
/// </summary>
public sealed class IvrStageDocument
{
    /// <summary>Stage identifier, unique within the workflow.</summary>
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Stage kind. Defaults to <c>stage</c> (a normal IVR stage with prompts / tools /
    /// transitions). When set to <c>subflow</c> the stage acts as a delegation marker
    /// that pushes a child workflow onto the navigator's frame stack; the
    /// <see cref="Subflow"/> block, <see cref="OnSuccess"/>, and <see cref="OnFailure"/>
    /// configure the delegation.
    /// </summary>
    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    /// <summary>
    /// Child-workflow reference for <c>type: subflow</c> stages. Ignored for normal stages.
    /// </summary>
    [YamlMember(Alias = "subflow")]
    public IvrSubflowReferenceDocument? Subflow { get; set; }

    /// <summary>
    /// Phase 2: import a stage from another workflow at compile time. When set the
    /// other stage fields (<c>realtime</c>, <c>scripted</c>, etc.) are ignored — the
    /// imported stage is cloned and inlined under <see cref="IvrStageImportDocument.As"/>
    /// (or the source stage id when not aliased). Only leaf stages are importable.
    /// </summary>
    [YamlMember(Alias = "import")]
    public IvrStageImportDocument? Import { get; set; }

    /// <summary>Optional human-readable description of the stage purpose.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>Stage goal sentence (e.g. "Greet caller and capture primary intent.").</summary>
    [YamlMember(Alias = "goal")]
    public string? Goal { get; set; }

    /// <summary>Preconditions (auth level, required state, previous-stage, predicate) gating entry.</summary>
    [YamlMember(Alias = "requires")]
    public List<IvrGuardDocument> Requires { get; set; } = [];

    /// <summary>Per-stage strategy override; falls back to the workflow strategy when absent.</summary>
    [YamlMember(Alias = "strategy")]
    public IvrStrategyDocument? Strategy { get; set; }

    /// <summary>Realtime agent configuration (prompt instructions, examples, tool-rule overrides).</summary>
    [YamlMember(Alias = "realtime")]
    public IvrRealtimeStageDocument? Realtime { get; set; }

    /// <summary>
    /// Scripted (non-generative) configuration shared by the DTMF and NLU tiers.
    /// Hosts the common prompt surface and policy knobs; per-tier override blocks
    /// (<c>scripted.nlu</c> / <c>scripted.dtmf</c>) carry only what genuinely differs.
    /// When absent, scripted-tier stages fall back to defaults supplied by the active strategy.
    /// </summary>
    [YamlMember(Alias = "scripted")]
    public IvrScriptedStageDocument? Scripted { get; set; }

    /// <summary>Locally-declared intents scoped to this stage.</summary>
    [YamlMember(Alias = "intents")]
    public List<IvrIntentDocument> Intents { get; set; } = [];

    /// <summary>Capability identifiers this stage exposes (resolved against the workflow capability table).</summary>
    [YamlMember(Alias = "capabilities")]
    public List<string> Capabilities { get; set; } = [];

    /// <summary>Conversational exit condition for the realtime agent.</summary>
    [YamlMember(Alias = "exitWhen")]
    public string? ExitWhen { get; set; }

    /// <summary>Next stage id when this stage exits normally.</summary>
    [YamlMember(Alias = "onExit")]
    public string? OnExit { get; set; }

    /// <summary>
    /// For <c>type: subflow</c> stages: parent-frame step id to enter after the child
    /// workflow exits via a non-failure terminal stage. Takes precedence over
    /// <see cref="IvrSubflowReferenceDocument.OnSuccess"/> when both are set.
    /// </summary>
    [YamlMember(Alias = "onSuccess")]
    public string? OnSuccess { get; set; }

    /// <summary>
    /// For <c>type: subflow</c> stages: parent-frame step id to enter after the child
    /// workflow exits via a failure terminal stage. Takes precedence over
    /// <see cref="IvrSubflowReferenceDocument.OnFailure"/> when both are set.
    /// </summary>
    [YamlMember(Alias = "onFailure")]
    public string? OnFailure { get; set; }

    /// <summary>
    /// Phase 3: stage-level override for the workflow's <c>onUnauthorized</c> fallback.
    /// When a transition into this stage fails its <c>requires:</c> guards and no
    /// auth-resolver chain can satisfy them, the navigator routes here instead of the
    /// workflow-default <c>onUnauthorized</c>.
    /// </summary>
    [YamlMember(Alias = "onUnauthorized")]
    public string? OnUnauthorized { get; set; }

    /// <summary>Marks a terminal stage; the workflow completes upon entry/exit.</summary>
    [YamlMember(Alias = "terminal")]
    public bool Terminal { get; set; }

    /// <summary>
    /// For terminal stages of a sub-workflow: <c>success</c> (default) routes the parent
    /// to the subflow stage's <c>onSuccess</c> target on pop; <c>failure</c> routes to
    /// <c>onFailure</c>. Ignored for non-terminal stages and for the root workflow.
    /// </summary>
    [YamlMember(Alias = "terminalOutcome")]
    public string? TerminalOutcome { get; set; }

    /// <summary>Maximum retries before failing the stage. Defaults to <c>3</c> when absent.</summary>
    [YamlMember(Alias = "maxRetries")]
    public int? MaxRetries { get; set; }

    /// <summary>Maximum duration (TimeSpan) the stage may run before forced escalation.</summary>
    [YamlMember(Alias = "maxDuration")]
    public string? MaxDuration { get; set; }

    /// <summary>State keys this stage commits to before exiting.</summary>
    [YamlMember(Alias = "collects")]
    public List<string> Collects { get; set; } = [];

    /// <summary>State keys that must already be present for the stage to enter.</summary>
    [YamlMember(Alias = "requiredState")]
    public List<string> RequiredState { get; set; } = [];

    /// <summary>Explicit valid transitions (in addition to any inferred from intents/capabilities).</summary>
    [YamlMember(Alias = "transitions")]
    public List<IvrTransitionDocument> Transitions { get; set; } = [];
}

/// <summary>Realtime portion of a stage.</summary>
public sealed class IvrRealtimeStageDocument
{
    /// <summary>Free-form realtime prompt instructions for this stage.</summary>
    [YamlMember(Alias = "instructions")]
    public List<string> Instructions { get; set; } = [];

    /// <summary>Example utterances that prime the agent's tone.</summary>
    [YamlMember(Alias = "examples")]
    public List<string> Examples { get; set; } = [];

    /// <summary>Stage-scoped tools (in addition to those from referenced capabilities).</summary>
    [YamlMember(Alias = "tools")]
    public List<string> Tools { get; set; } = [];

    /// <summary>Per-tool usage rules for the realtime agent.</summary>
    [YamlMember(Alias = "toolRules")]
    public List<IvrToolRuleDocument> ToolRules { get; set; } = [];
}

/// <summary>Explicit declarative transition. Either <see cref="OnIntent"/> or <see cref="OnCondition"/> should be set.</summary>
public sealed class IvrTransitionDocument
{
    [YamlMember(Alias = "to")]
    public string To { get; set; } = string.Empty;

    [YamlMember(Alias = "onIntent")]
    public string? OnIntent { get; set; }

    [YamlMember(Alias = "onCondition")]
    public string? OnCondition { get; set; }

    /// <summary>
    /// Phase 3: guards that must pass before this transition fires. Combined with the
    /// target stage's <c>requires:</c> at evaluation time. When any guard fails the
    /// navigator looks up a matching <see cref="IvrAuthResolverDocument"/> and detours
    /// through the named sub-workflow before re-applying the transition.
    /// </summary>
    [YamlMember(Alias = "requires")]
    public List<IvrGuardDocument> Requires { get; set; } = [];
}
