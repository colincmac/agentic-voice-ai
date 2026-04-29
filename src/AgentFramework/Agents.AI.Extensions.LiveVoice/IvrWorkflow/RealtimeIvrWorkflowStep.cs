using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.LiveVoice.IvrWorkflow;

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
    /// Gets the guards that must pass before this step can execute.
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
    /// Gets the DTMF menu options for this step, used when the session is operating
    /// in Tier 4 (pure DTMF) mode. Each entry maps a DTMF digit character
    /// ('0'-'9', '*', '#') to a menu label or action identifier.
    /// </summary>
    /// <remarks>
    /// When null, the DTMF transport collects free-form digit sequences instead of
    /// presenting a menu. Steps that require natural language input will be skipped
    /// with a warning in DTMF-only mode.
    /// </remarks>
    public IReadOnlyDictionary<char, string>? DtmfMenuOptions { get; init; }

    /// <summary>
    /// Gets the valid step IDs this step can transition to.
    /// </summary>
    public IReadOnlyList<string> ValidTransitions =>
        ConversationState.Transitions?.Select(t => t.NextStep).ToList() ?? [];

}

public readonly struct IvrStepAgentConfiguration(string Instructions, IEnumerable<AITool>? Tools = null)
{
    public string Instructions { get; } = Instructions;
    public IEnumerable<AITool>? Tools { get; } = Tools;
};
