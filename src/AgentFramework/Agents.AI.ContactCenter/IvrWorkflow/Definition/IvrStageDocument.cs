using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Definition;

/// <summary>
/// A workflow stage. Stages own conversation state, may declare a DTMF menu, a realtime
/// configuration, locally-scoped intents, and references to shared capabilities. Stage
/// authors typically pick one of:
/// <list type="bullet">
///   <item>Pure DTMF stage (no realtime, only <see cref="Dtmf"/>),</item>
///   <item>Pure realtime stage (only <see cref="Realtime"/>),</item>
///   <item>Mixed (both blocks; the strategy selector chooses at runtime).</item>
/// </list>
/// </summary>
public sealed class IvrStageDocument
{
    /// <summary>Stage identifier, unique within the workflow.</summary>
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = string.Empty;

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

    /// <summary>DTMF configuration (menu options, digit collection, validators, prompts).</summary>
    [YamlMember(Alias = "dtmf")]
    public IvrDtmfDocument? Dtmf { get; set; }

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

    /// <summary>Marks a terminal stage; the workflow completes upon entry/exit.</summary>
    [YamlMember(Alias = "terminal")]
    public bool Terminal { get; set; }

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
}
