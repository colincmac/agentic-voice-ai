using System.Text;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
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
    /// Gets the tools available during this step.
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
    /// Gets the valid step IDs this step can transition to.
    /// </summary>
    public IReadOnlyList<string> ValidTransitions =>
        ConversationState.Transitions?.Select(t => t.NextStep).ToList() ?? [];
}

/// <summary>
/// Represents a complete Realtime IVR workflow definition that integrates
/// with the Microsoft Agent Framework Workflow SDK.
/// </summary>
public sealed class RealtimeIvrWorkflowDefinition
{
    /// <summary>
    /// Gets the workflow name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the base prompt configuration shared across all steps.
    /// </summary>
    public required RealtimePrompt BasePrompt { get; init; }

    /// <summary>
    /// Gets the ordered workflow steps.
    /// </summary>
    public required IReadOnlyList<RealtimeIvrWorkflowStep> Steps { get; init; }

    /// <summary>
    /// Gets the initial step ID.
    /// </summary>
    public string InitialStepId => Steps.FirstOrDefault()?.Id ?? throw new InvalidOperationException("Workflow has no steps");

    /// <summary>
    /// Gets a step by ID.
    /// </summary>
    public RealtimeIvrWorkflowStep? GetStep(string stepId) =>
        Steps.FirstOrDefault(s => s.Id == stepId);

    /// <summary>
    /// Gets the index of a step by ID.
    /// </summary>
    public int GetStepIndex(string stepId) =>
        Steps.ToList().FindIndex(s => s.Id == stepId);

    /// <summary>
    /// Builds the system prompt for a specific step, including only the tools available for that step.
    /// </summary>
    public string BuildPromptForStep(string stepId, IvrWorkflowState? state = null)
    {
        var step = GetStep(stepId) ?? throw new ArgumentException($"Step '{stepId}' not found", nameof(stepId));

        // Build a step-specific prompt by merging base prompt with step configuration
        var stepPrompt = BasePrompt with
        {
            ConversationFlow = [step.ConversationState],
            Tools = BuildToolConfigForStep(step),
            Context = BuildContextForStep(step, state)
        };

        return RealtimeAIPromptTemplate.Render(stepPrompt);
    }

    /// <summary>
    /// Gets the tools available for a specific step.
    /// </summary>
    public IReadOnlyList<AITool> GetToolsForStep(string stepId)
    {
        var step = GetStep(stepId);

        return step?.AvailableTools ?? [];
    }

    private ToolConfiguration? BuildToolConfigForStep(RealtimeIvrWorkflowStep step)
    {
        if (step.ToolRules is null or { Count: 0 } && BasePrompt.Tools is null)
        {
            return null;
        }

        return new ToolConfiguration
        {
            GlobalPreamble = BasePrompt.Tools?.GlobalPreamble,
            RequireConfirmation = BasePrompt.Tools?.RequireConfirmation ?? false,
            ToolRules = step.ToolRules ?? BasePrompt.Tools?.ToolRules,
            SupervisorTool = BasePrompt.Tools?.SupervisorTool
        };
    }

    private string? BuildContextForStep(RealtimeIvrWorkflowStep step, IvrWorkflowState? state)
    {
        if (state is null)
        {
            return BasePrompt.Context;
        }

        var contextBuilder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(BasePrompt.Context))
        {
            contextBuilder.AppendLine(BasePrompt.Context);
        }

        // Add collected state as context
        if (state.CompletedSteps.Count > 0)
        {
            contextBuilder.AppendLine();
            contextBuilder.AppendLine("## Collected Information");

            foreach (var key in step.RequiredStateKeys)
            {
                if (state.TryGet<object>(key, out var value))
                {
                    contextBuilder.AppendLine($"- {key}: {value}");
                }
            }
        }

        var result = contextBuilder.ToString().Trim();

        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
