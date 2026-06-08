using System.Text;
using Agents.AI.ContactCenter.Authentication;
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
    /// <param name="workflow">Compiled workflow that owns <paramref name="stage"/>.</param>
    /// <param name="stage">Active stage whose prompt to render.</param>
    /// <param name="state">Optional collected workflow state; included under "Collected information".</param>
    /// <param name="identity">
    /// Optional caller identity resolved by the call-start authenticator chain. When supplied
    /// and the identity has reached <see cref="CallerVerificationLevel.AniMatch"/> or higher,
    /// a "Caller hint (unverified)" section is appended that surfaces the matched name / phone
    /// to the model alongside explicit guidance to confirm the identity with the caller before
    /// relying on it (caller IDs can be spoofed or shared between users). When <see langword="null"/>,
    /// anonymous, or below AniMatch, no hint section is emitted and the output is identical to
    /// the prior overload.
    /// </param>
    public static string RenderRealtimePrompt(
        CompiledCallWorkflow workflow,
        CompiledStage stage,
        IvrWorkflowState? state = null,
        CallerIdentity? identity = null)
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

        AppendCallerHint(sb, identity);

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Append the "Caller hint (unverified)" section when <paramref name="identity"/> is a real,
    /// at-least-ANI-matched identity. The block lists the matched name, phone, verification level,
    /// and source authenticator, then states explicitly that the match comes from a passive signal
    /// (caller ID) that may be wrong, and that the model must confirm the name with the caller
    /// before relying on the hint.
    /// </summary>
    private static void AppendCallerHint(StringBuilder sb, CallerIdentity? identity)
    {
        if (identity is null
            || identity.VerificationLevel < CallerVerificationLevel.AniMatch
            || string.Equals(identity.UserId, "anonymous", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        sb.AppendLine("## Caller hint (unverified)");
        if (!string.IsNullOrWhiteSpace(identity.DisplayName))
        {
            sb.AppendLine($"- Name: {identity.DisplayName}");
        }
        if (!string.IsNullOrWhiteSpace(identity.PhoneNumber))
        {
            sb.AppendLine($"- Phone: {identity.PhoneNumber}");
        }
        sb.AppendLine($"- Verification level: {identity.VerificationLevel}");
        if (!string.IsNullOrWhiteSpace(identity.AuthenticatedBy))
        {
            sb.AppendLine($"- Source: {identity.AuthenticatedBy}");
        }
        sb.AppendLine();
        sb.AppendLine(
            "This match is from a passive signal (caller ID) and may be wrong — caller IDs can be spoofed or shared. " +
            "Do not assume the caller is this person. Use the hint to confirm their name (e.g. \"I'm showing this call from " +
            $"{identity.DisplayName} — is that you?\"). If they disagree or you're unsure, fall back to asking for their " +
            "first and last name as usual. Always call `record_caller_name` once the caller has stated their name.");
        sb.AppendLine();
    }


}
