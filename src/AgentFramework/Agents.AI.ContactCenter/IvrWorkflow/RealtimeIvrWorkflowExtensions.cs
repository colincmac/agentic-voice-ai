using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

namespace Agents.AI.ContactCenter.IvrWorkflow;

public static class RealtimeIvrWorkflowExtensions
{
    public static IvrStepAgentConfiguration GetArtStepConfiguration(this RealtimeIvrWorkflowDefinition workflow, RealtimeIvrWorkflowStep step, IvrWorkflowState? state = null, JsonSerializerOptions? contextSerializerOptions = null)
    {
        
        // Build a step-specific prompt by merging base prompt with step configuration
        var stepPrompt = workflow.BuildPromptForStep(step, state, conversationContext: null, contextSerializerOptions);

        return new IvrStepAgentConfiguration(stepPrompt, step.AvailableTools);
    }
}
