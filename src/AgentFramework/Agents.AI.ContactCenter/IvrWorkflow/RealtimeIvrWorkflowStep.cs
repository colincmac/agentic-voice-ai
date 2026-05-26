using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// Represents a workflow step that integrates with the Realtime AI prompt system.
/// Each step defines a conversation state, required tools, guards, and exit conditions.
/// </summary>
public sealed class RealtimeIvrWorkflowStep
{
    /// <summary>
    /// Gets the unique identifier for this step.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the conversation state configuration for the Realtime AI agent prompt.
    /// </summary>
    public required ConversationState ConversationState { get; init; }

    /// <summary>
    /// Gets the tools available for the Talker/Interacting Voice Agent during this step.
    /// Tools are gated per-step to prevent premature access (e.g., can't activate card until PIN verified).
    /// </summary>
    public IReadOnlyList<AITool>? AvailableTools { get; init; }

    /// <summary>
    /// Gets the tool usage rules for this step's prompt.
    /// </summary>
    public IReadOnlyList<ToolUsageRule>? ToolRules { get; init; }

    /// <summary>
    /// Gets the guards that gate any tool invocation on this step. When a guard fails,
    /// the tool call is blocked and the failure reason is surfaced to the caller — as a
    /// <see cref="DtmfActionResult.Reject"/> on the DTMF path (via <see cref="IIvrWorkflowNavigator.InvokeActionAsync"/>),
    /// or as a string tool result on the LLM/realtime path (via <see cref="GuardedAIFunction"/>
    /// and <see cref="IIvrWorkflowNavigator.WrapToolsWithCurrentGuards"/>). Built-in guards
    /// check workflow state (e.g., <see cref="RequiredStateGuard"/>,
    /// <see cref="PreviousStepCompletedGuard"/>); custom guards can use any predicate over
    /// <see cref="IvrWorkflowState"/>.
    /// </summary>
    public IReadOnlyList<IIvrStepGuard> Guards { get; init; } = [];

    /// <summary>
    /// Gets the validators that check if this step's requirements are satisfied.
    /// </summary>
    public IReadOnlyList<IIvrStepValidator> Validators { get; init; } = [];

    /// <summary>
    /// Gets the state keys that must be collected before exiting this step.
    /// </summary>
    public IReadOnlyList<string> RequiredStateKeys { get; init; } = [];

    /// <summary>
    /// Gets the maximum number of retries allowed for this step.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Gets the maximum duration for this step before escalation.
    /// </summary>
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>
    /// Gets the required authentication level for this step.
    /// </summary>
    public AuthenticationLevel RequiredAuthLevel { get; init; } = AuthenticationLevel.None;

    /// <summary>
    /// Gets an optional callback executed when this step completes successfully.
    /// </summary>
    public Func<IvrWorkflowState, CancellationToken, Task>? OnCompleted { get; init; }

    /// <summary>
    /// Gets the scripted (non-generative) configuration for this step, used when the
    /// session is operating in a DTMF or NLU tier. Hosts shared prompts and policy knobs
    /// at the root, plus optional per-tier sub-configurations under
    /// <see cref="StepScriptedConfiguration.Nlu"/> and <see cref="StepScriptedConfiguration.Dtmf"/>.
    /// </summary>
    /// <remarks>
    /// When null, the stage is generative-only (realtime tier) or relies entirely on
    /// host-supplied strategy defaults. Steps that require natural language input will be
    /// skipped with a warning in DTMF-only mode when their DTMF sub-configuration is also null.
    /// </remarks>
    public StepScriptedConfiguration? StepScriptedConfiguration { get; init; }

    /// <summary>
    /// Indicates whether this step is terminal — i.e. once the workflow enters this stage
    /// no further transitions are expected. Strategies use this to decide whether to expose
    /// the synthetic advance tool to the model and when to wind the session down. Populated
    /// by <c>IvrWorkflowCompiler</c> from the YAML <c>terminal:</c> flag; defaults to
    /// <see langword="false"/> for steps built directly in code.
    /// </summary>
    public bool Terminal { get; init; }

    /// <summary>
    /// Gets the valid step IDs this step can transition to.
    /// </summary>
    public IReadOnlyList<string> ValidTransitions =>
        ConversationState.Transitions?.Select(t => t.NextStep).ToList() ?? [];

    /// <summary>
    /// Gets the compiled intent table for this step keyed by intent name. Populated by the
    /// declarative IVR workflow compiler from the YAML <c>intents:</c> block; consumed by
    /// the NLU strategy (to enumerate intent names and per-intent examples) and by the
    /// realtime strategy (to synthesize an <c>advance</c> tool with the allowed intents).
    /// Empty for runtime steps built directly in code without going through the compiler.
    /// </summary>
    public IReadOnlyDictionary<string, RealtimeIvrWorkflowIntent> Intents { get; init; } =
        new Dictionary<string, RealtimeIvrWorkflowIntent>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Compiled intent metadata attached to a <see cref="RealtimeIvrWorkflowStep"/>. Mirrors
/// <c>Compilation.CompiledIvrIntent</c> but lives on the runtime step so strategies can
/// consume it without taking a dependency on the compiler types.
/// </summary>
/// <param name="Name">Intent name (e.g. <c>balance</c>).</param>
/// <param name="Examples">Example utterances seeded by the YAML and used by keyword/NLU classifiers.</param>
/// <param name="NextStepId">Optional next stage id the workflow transitions to when this intent fires.</param>
/// <param name="CapabilityId">Optional capability the intent should invoke.</param>
/// <param name="ConfirmPrompt">Optional confirmation prompt the orchestrator should speak before the transition.</param>
public sealed record RealtimeIvrWorkflowIntent(
    string Name,
    IReadOnlyList<string> Examples,
    string? NextStepId = null,
    string? CapabilityId = null,
    string? ConfirmPrompt = null);

public readonly struct IvrStepAgentConfiguration(string Instructions, IEnumerable<AITool>? Tools = null)
{
    public string Instructions { get; } = Instructions;
    public IEnumerable<AITool>? Tools { get; } = Tools;
};
