using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Agents.AI.Extensions.AITools;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.IvrWorkflow;

public sealed class IvrWorkflowTools : IAIToolCollection
{

    private readonly RealtimeIvrWorkflowStep _currentStep;
    private readonly Action<OrchestratorAction> _onAction;
    private readonly List<AITool> _tools = [];
    private readonly JsonSerializerOptions? _jsonSerializerOptions = LiveVoiceJsonUtilities.DefaultOptions;
    public IvrWorkflowTools(
        RealtimeIvrWorkflowStep currentStep,
        Action<OrchestratorAction> onAction,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        _currentStep = currentStep;
        _onAction = onAction;
        _jsonSerializerOptions = jsonSerializerOptions;
        BuildTools();
    }

    private void BuildTools()
    {
        // Add escalation tool
        _tools.Add(AIFunctionFactory.Create(EscalateAsync, serializerOptions: _jsonSerializerOptions));

        // Add data extraction tool
        _tools.Add(AIFunctionFactory.Create(ExtractDataAsync, serializerOptions: _jsonSerializerOptions));

        // Add sentiment reporting tool
        _tools.Add(AIFunctionFactory.Create(ReportSentimentAsync, serializerOptions: _jsonSerializerOptions));

        // Add end workflow tool
        _tools.Add(AIFunctionFactory.Create(EndWorkflowAsync, serializerOptions: _jsonSerializerOptions));

        // Dynamically generate transition tools based on current step's valid transitions
        if (_currentStep.ConversationState.Transitions is { Count: > 0 } transitions)
        {
            foreach (var transition in transitions)
            {
                var tool = CreateTransitionTool(transition);
                _tools.Add(tool);
            }
        }
    }

    private AIFunction CreateTransitionTool(StateTransition transition)
    {
        var functionName = $"transition_to_{transition.NextStep}";
        var description = $"Transition to the '{transition.NextStep}' step when: {transition.Condition}";

        return AIFunctionFactory.Create(
            ([Description("Brief explanation for why this transition is appropriate")] string reason) =>
            {
                _onAction(new TransitionAction(transition.NextStep, reason));
                return Task.FromResult($"Transitioning to {transition.NextStep}");
            },
            name: functionName,
            description: description,
            serializerOptions: _jsonSerializerOptions);
    }

    [Description("Escalate to a human agent when the user explicitly requests assistance, expresses significant frustration, or the situation requires human intervention.")]
    public Task<string> EscalateAsync(
        [Description("The reason for escalating to a human agent")] string reason)
    {
        _onAction(new EscalateAction(reason));
        return Task.FromResult("Escalating to human agent");
    }

    [Description("Extract and store data collected from the user during this conversation turn.")]
    public Task<string> ExtractDataAsync(
        [Description("The data key (e.g., 'accountNumber', 'pin', 'intent')")] string key,
        [Description("The extracted value from the user's response")] string value)
    {
        _onAction(new ExtractDataAction(key, value));
        return Task.FromResult($"Stored {key}");
    }

    [Description("Report the detected sentiment of the user in this turn. Call this to track customer satisfaction.")]
    public Task<string> ReportSentimentAsync(
        [Description("Sentiment score from -1.0 (very negative) to 1.0 (very positive), 0.0 for neutral")] double score)
    {
        _onAction(new SentimentAction(score));
        return Task.FromResult($"Sentiment recorded: {score}");
    }

    [Description("End the workflow when the user's request has been fully resolved.")]
    public Task<string> EndWorkflowAsync(
        [Description("Summary of how the request was resolved")] string resolution)
    {
        _onAction(new EndWorkflowAction(resolution));
        return Task.FromResult("Workflow completed");
    }

    public IEnumerable<AITool> AsAITools() => _tools;
}

/// <summary>
/// Base type for orchestrator actions triggered by AI tool calls.
/// </summary>
public abstract record OrchestratorAction;

public sealed record TransitionAction(string NextStepId, string Reason) : OrchestratorAction;

public sealed record EscalateAction(string Reason) : OrchestratorAction;

public sealed record ExtractDataAction(string Key, string Value) : OrchestratorAction;

public sealed record SentimentAction(double Score) : OrchestratorAction;

public sealed record EndWorkflowAction(string Resolution) : OrchestratorAction;
