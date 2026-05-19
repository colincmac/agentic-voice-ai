using System.Collections.Generic;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;
using Agents.AI.ContactCenter.IvrWorkflow.Strategies;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>
/// Runtime-ready representation of a YAML IVR workflow. Produced by
/// <see cref="IIvrWorkflowCompiler"/> and consumed by
/// <c>CallSessionFactory</c>/<c>IConversationStrategyFactory</c> implementations.
/// </summary>
public sealed class CompiledIvrWorkflow
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public int Version { get; init; }

    /// <summary>The legacy runtime model the existing strategy factories consume directly.</summary>
    public required RealtimeIvrWorkflowDefinition Runtime { get; init; }

    /// <summary>Workflow-level strategy policy (initial mode, fallbacks, prewarm tiers, degradation).</summary>
    public required IvrStrategyPolicy Strategy { get; init; }

    /// <summary>Compiled stages keyed by stage id. Iteration order matches the YAML order.</summary>
    public required IReadOnlyList<CompiledIvrStage> Stages { get; init; }

    /// <summary>Compiled capability table keyed by capability id.</summary>
    public required IReadOnlyDictionary<string, CompiledIvrCapability> Capabilities { get; init; }

    /// <summary>Intent examples aggregated across the workflow. Keyed by intent name.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> IntentExamples { get; init; }

    /// <summary>Provenance metadata from the source (when known).</summary>
    public IvrWorkflowSourceEntry? Source { get; init; }
}

/// <summary>Compiled stage data: tool set, policy, capability handles, intent routing, runtime step.</summary>
public sealed class CompiledIvrStage
{
    public required string Id { get; init; }
    public string? Description { get; init; }
    public string? Goal { get; init; }
    public bool Terminal { get; init; }

    public required IvrStrategyPolicy Strategy { get; init; }

    /// <summary>Tools resolved from the YAML tool names + capability tools + base common tools.</summary>
    public required IReadOnlyList<AITool> Tools { get; init; }

    /// <summary>Capability ids exposed in this stage.</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>Intent name -> compiled intent (with examples and routing).</summary>
    public required IReadOnlyDictionary<string, CompiledIvrIntent> Intents { get; init; }

    /// <summary>Runtime step that already includes guards, tool rules, DTMF config, and prompt state.</summary>
    public required RealtimeIvrWorkflowStep RuntimeStep { get; init; }
}

/// <summary>Compiled capability metadata used by the runtime to expose business actions.</summary>
public sealed class CompiledIvrCapability
{
    public required string Id { get; init; }
    public string? Description { get; init; }
    public required IReadOnlyList<AITool> Tools { get; init; }
    public required IReadOnlyList<IIvrStepGuard> Guards { get; init; }
}

/// <summary>Compiled intent metadata used by intent classifiers and routing.</summary>
public sealed class CompiledIvrIntent
{
    public required string Name { get; init; }
    public IReadOnlyList<string> Examples { get; init; } = [];
    public string? NextStageId { get; init; }
    public string? CapabilityId { get; init; }
    public string? ConfirmPrompt { get; init; }
}
