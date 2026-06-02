using System.Text;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;

namespace Agents.AI.ContactCenter.IvrWorkflow.Navigation;

/// <summary>
/// Pure functions that render a stage's authored business prompts (from
/// <see cref="StageBlueprint.Channels"/>) into a Markdown system-prompt fragment suitable
/// for the realtime tier. Keeps the navigator's responsibility tight (graph walking) by
/// pulling prompt assembly into a stateless helper.
/// </summary>
/// <remarks>
/// The output shape follows the OpenAI Realtime Prompting Guide's "Dynamic Conversation
/// Flow" advice: a small per-state system prompt scoped to the active stage. The host's
/// own base prompt (e.g. brand persona, safety) prepends the workflow's
/// <see cref="WorkflowBlueprint.BasePrompt"/>, which then prepends every per-stage prompt.
/// </remarks>
public static class StagePromptRenderer
{
    /// <summary>Render the realtime-tier prompt for <paramref name="stage"/>.</summary>
    public static string RenderRealtimePrompt(
        CompiledCallWorkflow workflow,
        CompiledStage stage,
        IvrWorkflowState? state = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(stage);

        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(workflow.Blueprint.BasePrompt))
        {
            sb.AppendLine(workflow.Blueprint.BasePrompt.Trim());
            sb.AppendLine();
        }

        sb.AppendLine($"# Current Stage: {stage.Id}");
        if (!string.IsNullOrWhiteSpace(stage.Blueprint.Goal))
        {
            sb.AppendLine($"- Goal: {stage.Blueprint.Goal}");
        }
        if (!string.IsNullOrWhiteSpace(stage.Blueprint.Description))
        {
            sb.AppendLine($"- Description: {stage.Blueprint.Description}");
        }
        if (!string.IsNullOrWhiteSpace(stage.Blueprint.ExitCondition))
        {
            sb.AppendLine($"- Exit when: {stage.Blueprint.ExitCondition}");
        }
        sb.AppendLine();

        if (stage.Blueprint.Channels.Realtime is { } realtime)
        {
            if (realtime.Instructions.Count > 0)
            {
                sb.AppendLine("## Instructions");
                foreach (var instruction in realtime.Instructions)
                {
                    sb.AppendLine($"- {instruction}");
                }
                sb.AppendLine();
            }

            if (realtime.Examples.Count > 0)
            {
                sb.AppendLine("## Example utterances");
                foreach (var example in realtime.Examples)
                {
                    sb.AppendLine($"- \"{example}\"");
                }
                sb.AppendLine();
            }
        }

        if (stage.OutgoingEdges.Count > 0 && !stage.Terminal)
        {
            sb.AppendLine("## Available transitions");
            sb.AppendLine("Call `advance` with the matching `target` label when its condition is met:");
            foreach (var edge in stage.OutgoingEdges)
            {
                var hint = !string.IsNullOrWhiteSpace(edge.Blueprint.When)
                    ? $" — {edge.Blueprint.When}"
                    : string.Empty;
                sb.AppendLine($"- `{edge.Label}` → stage `{edge.TargetStageId}`{hint}");
            }
            sb.AppendLine();
        }

        if (state is { Keys.Count: > 0 })
        {
            sb.AppendLine("## Collected information");
            foreach (var key in state.Keys)
            {
                var value = state.Get<object>(key);
                sb.AppendLine($"- {key}: {value ?? "<null>"}");
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
