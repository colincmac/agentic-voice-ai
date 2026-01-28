using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.LiveVoice.IvrWorkflow;

public static class RealtimeIvrWorkflowBuilderExtensions
{

    /// <summary>
    /// Adds a set of default tools to the specified real-time IVR workflow builder to handle common IVR scenarios,
    /// including supervisor handoff, escalation to a human agent, and session completion.
    /// See: https://cookbook.openai.com/examples/realtime_prompting_guide#common-tools
    /// </summary>
    /// <remarks>Use this method to quickly configure a real-time IVR workflow with standard tools for
    /// handling supervisor handoff, escalation, and session completion events. Custom callback functions can be
    /// provided to implement specific logic for each event.</remarks>
    /// <param name="builder">The workflow builder to which the default tools will be added. Cannot be null.</param>
    /// <param name="onHandoffQuery">An optional asynchronous callback that is invoked when a supervisor handoff is requested. Receives the handoff
    /// query as a parameter.</param>
    /// <param name="onEscalationRequested">An optional asynchronous callback that is invoked when an escalation to a human agent is requested.</param>
    /// <param name="onSessionFinished">An optional asynchronous callback that is invoked when the IVR session is finished.</param>
    /// <returns>The same workflow builder instance with the default tools added.</returns>
    public static RealtimeIvrWorkflowBuilder AddDefaultTools(this RealtimeIvrWorkflowBuilder builder,
        Func<string, ValueTask>? onHandoffQuery = null,
        Func<ValueTask>? onEscalationRequested = null,
        Func<ValueTask>? onSessionFinished = null)
    {
        builder.WithCommonTools(
            [
                CreateSupervisorAnswerTool(onHandoffQuery),
                CreateEscalateToHumanTool(onEscalationRequested),
                CreateFinishSessionTool(onSessionFinished)
            ]);

        return builder;
    }

    private static AIFunction CreateSupervisorAnswerTool(Func<string, ValueTask>? onHandoffQuery = null)
    {
        var description = "Call this when the customer asks a question that you don't have an answer to or asks to perform an action.";

        return AIFunctionFactory.Create(
            async ([Description("Summary of the users query, with any relevant details, to hand off to another AI Agent.")] string question) =>
            {
                if (onHandoffQuery is not null)
                {
                    await onHandoffQuery(question);
                }
                return Task.FromResult($"Query sent for processing.");
            },
            name: "answer",
            description: description);
    }
    private static AIFunction CreateEscalateToHumanTool(Func<ValueTask>? onEscalationRequested = null)
    {
        var description = "Call this when a customer asks for escalation, or to talk to someone else, or expresses dissatisfaction with the call.";

        return AIFunctionFactory.Create(
            async () =>
            {
                if (onEscalationRequested is not null)
                {
                    await onEscalationRequested();
                }
                return "Escalation requested.";
            },
            name: "escalate_to_human",
            description: description);
    }

    private static AIFunction CreateFinishSessionTool(Func<ValueTask>? onSessionFinished = null)
    {
        var description = "Call this when a customer says they're done with the session or doesn't want to continue. If it's ambiguous, confirm with the customer before calling.";

        return AIFunctionFactory.Create(
            async () =>
            {
                if (onSessionFinished is not null)
                {
                    await onSessionFinished();
                }
                return Task.FromResult("Session finished.");
            },
            name: "finish_session",
            description: description);
    }
}
