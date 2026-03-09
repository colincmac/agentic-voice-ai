using System.Text;
using System.Text.Json;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

namespace Agents.AI.Extensions.LiveVoice.IvrWorkflow;

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
    public string InitialStepId => Steps[0]?.Id ?? throw new InvalidOperationException("Workflow has no steps");

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
    /// Throws <see cref="ArgumentException"/> if the step ID is not found.
    /// </summary>
    /// <param name="stepId">The workflow step ID.</param>
    /// <param name="state">Optional current workflow state for context inclusion.</param>
    /// <param name="contextSerializerOptions">Optional JSON serializer options for formatting state values in context.</param>
    /// <returns>The rendered prompt string for the Voice AI Agent (e.g. <i>talker</i> agent).</returns>
    public string BuildPromptForStep(string stepId, IvrWorkflowState? state = null, JsonSerializerOptions? contextSerializerOptions = null)
    {
        var step = GetStep(stepId) ?? throw new ArgumentException($"Step '{stepId}' not found", nameof(stepId));

        return BuildPromptForStep(step, state, contextSerializerOptions);
    }

    /// <summary>
    /// Builds the system prompt for a specific step, including only the tools available for that step.
    /// </summary>
    /// <param name="step">The workflow step.</param>
    /// <param name="state">Optional current workflow state for context inclusion.</param>
    /// <param name="contextSerializerOptions">Optional JSON serializer options for formatting state values in context.</param>
    /// <returns>The rendered prompt string for the Voice AI Agent (e.g. <i>talker</i> agent).</returns>
    public string BuildPromptForStep(RealtimeIvrWorkflowStep step, IvrWorkflowState? state = null, JsonSerializerOptions? contextSerializerOptions = null)
    {
        // Build a step-specific prompt by merging base prompt with step configuration
        var stepPrompt = BasePrompt with
        {
            ConversationFlow = [step.ConversationState],
            Tools = BuildToolConfigForStep(step),
            Context = BuildContext(state, contextSerializerOptions)
        };

        return RealtimeAIPromptTemplate.Render(stepPrompt);
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

    private string? BuildContext(IvrWorkflowState? state, JsonSerializerOptions? jsonOptions = null)
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

        contextBuilder.AppendLine();
        contextBuilder.AppendLine("## Collected Information (formatted `- <key>: <value>`)");

        // Add collected state as context
        if (state.Keys.Count > 0)
        {
            foreach (var key in state.Keys)
            {
                if (state.TryGet<object>(key, out var value))
                {
                    var formattedValue = FormatStateValue(value, jsonOptions);

                    contextBuilder.AppendLine($"- {key}: {formattedValue}");
                }
            }
        }
        else
        {
            contextBuilder.AppendLine("- None");
        }

        var result = contextBuilder.ToString().Trim();

        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string FormatStateValue(object? value, JsonSerializerOptions? jsonOptions = null)
    {
        if (value is null)
        {
            return "(null)";
        }

        // Primitive types and strings can use ToString() directly
        if (value is string or bool or int or long or double or decimal or float)
        {
            return value.ToString() ?? "(null)";
        }

        // JsonElement from deserialized data
        if (value is JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                JsonValueKind.String => jsonElement.GetString() ?? "(null)",
                JsonValueKind.Number => jsonElement.GetRawText(),
                JsonValueKind.True or JsonValueKind.False => jsonElement.GetBoolean().ToString(),
                JsonValueKind.Null => "(null)",
                _ => jsonElement.GetRawText()
            };
        }

        // Complex objects - serialize to JSON
        try
        {
            return JsonSerializer.Serialize(value, jsonOptions);
        }
        catch
        {
            return value.ToString() ?? "(unknown)";
        }
    }
}
