using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Registry;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Registry;

public class IvrAdvanceToolTests
{
    [Fact]
    public void TryCreate_returns_null_when_step_has_no_transitions_or_intents()
    {
        var step = BuildStep(
            id: "leaf",
            transitions: [],
            intents: []);

        Assert.Null(IvrAdvanceTool.TryCreate(step));
    }

    [Fact]
    public void TryCreate_returns_tool_when_step_has_transitions()
    {
        var step = BuildStep(
            id: "menu",
            transitions: ["verify", "transfer"],
            intents: []);

        var tool = IvrAdvanceTool.TryCreate(step);
        Assert.NotNull(tool);
        Assert.Equal(IvrAdvanceTool.AdvanceToolName, tool!.Name);
        Assert.Contains("verify", tool.Description);
        Assert.Contains("transfer", tool.Description);
    }

    [Fact]
    public void CollectAdvanceTargets_emits_intent_names_before_raw_stage_ids()
    {
        var step = BuildStep(
            id: "menu",
            transitions: ["verify-account", "verify-identity"],
            intents:
            [
                new RealtimeIvrWorkflowIntent("balance", [], NextStepId: "verify-account"),
                new RealtimeIvrWorkflowIntent("activate-card", [], NextStepId: "verify-identity"),
            ]);

        var targets = IvrAdvanceTool.CollectAdvanceTargets(step);
        Assert.Equal(["balance", "activate-card", "verify-account", "verify-identity"], targets);
    }

    [Fact]
    public void Resolve_intent_name_maps_to_intents_next_step_id()
    {
        var step = BuildStep(
            id: "menu",
            transitions: ["verify-account"],
            intents:
            [
                new RealtimeIvrWorkflowIntent("balance", [], NextStepId: "verify-account"),
            ]);

        var result = IvrAdvanceTool.Resolve(step, "balance");
        Assert.Equal(AdvanceResolutionKind.Intent, result.Kind);
        Assert.True(result.IsTransition);
        Assert.Equal("verify-account", result.TargetStageId);
        Assert.Equal("balance", result.ResolvedIntent?.Name);
    }

    [Fact]
    public void Resolve_stage_id_maps_to_direct_transition()
    {
        var step = BuildStep(
            id: "menu",
            transitions: ["verify-account"],
            intents: []);

        var result = IvrAdvanceTool.Resolve(step, "verify-account");
        Assert.Equal(AdvanceResolutionKind.Stage, result.Kind);
        Assert.True(result.IsTransition);
        Assert.Equal("verify-account", result.TargetStageId);
    }

    [Fact]
    public void Resolve_intent_without_next_stage_returns_intent_without_transition()
    {
        var step = BuildStep(
            id: "menu",
            transitions: [],
            intents:
            [
                new RealtimeIvrWorkflowIntent("agent", [], CapabilityId: "transfer-to-human"),
            ]);

        var result = IvrAdvanceTool.Resolve(step, "agent");
        Assert.Equal(AdvanceResolutionKind.IntentWithoutTransition, result.Kind);
        Assert.False(result.IsTransition);
    }

    [Fact]
    public void Resolve_unknown_choice_returns_unknown()
    {
        var step = BuildStep(
            id: "menu",
            transitions: ["verify-account"],
            intents: []);

        var result = IvrAdvanceTool.Resolve(step, "not-a-real-target");
        Assert.Equal(AdvanceResolutionKind.Unknown, result.Kind);
        Assert.False(result.IsTransition);
    }

    private static RealtimeIvrWorkflowStep BuildStep(
        string id,
        string[] transitions,
        RealtimeIvrWorkflowIntent[] intents)
    {
        return new RealtimeIvrWorkflowStep
        {
            Id = id,
            ConversationState = new ConversationState
            {
                Id = id,
                Description = id,
                Goal = id,
                Instructions = [],
                Transitions = transitions.Length == 0
                    ? null
                    : [.. transitions.Select(t => new StateTransition { NextStep = t, Condition = "default" })],
            },
            Intents = intents.ToDictionary(i => i.Name, i => i, StringComparer.OrdinalIgnoreCase),
        };
    }
}
