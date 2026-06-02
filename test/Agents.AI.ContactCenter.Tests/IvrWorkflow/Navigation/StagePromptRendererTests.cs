using global::Agents.AI.ContactCenter.IvrWorkflow;
using global::Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using global::Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using global::Agents.AI.ContactCenter.IvrWorkflow.Navigation;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Navigation;

public sealed class StagePromptRendererTests
{
    private static CompiledCallWorkflow Compile() => new WorkflowGraphCompiler().Compile(new WorkflowBlueprint
    {
        Id = "renderer-test",
        BasePrompt = "You are ACME Bank concierge.",
        InitialStageId = "welcome",
        Stages =
        [
            new StageBlueprint
            {
                Id = "welcome",
                Goal = "Greet and capture intent.",
                Description = "Initial entry.",
                ExitCondition = "Caller stated intent.",
                Channels = new StageChannelConfig
                {
                    Realtime = new StageRealtimePrompt
                    {
                        Instructions = ["Greet warmly.", "Capture intent."],
                        Examples = ["Welcome to ACME Bank."],
                    },
                },
                Transitions =
                [
                    new TransitionBlueprint { TargetStageId = "next", Label = "balance", When = "Caller wants balance." },
                ],
            },
            new StageBlueprint { Id = "next", Terminal = true },
        ],
    });

    [Fact]
    public void RenderRealtimePrompt_IncludesBasePromptAndStageGoal()
    {
        var workflow = Compile();
        var rendered = StagePromptRenderer.RenderRealtimePrompt(workflow, workflow.InitialStage);

        Assert.Contains("ACME Bank concierge", rendered);
        Assert.Contains("# Current Stage: welcome", rendered);
        Assert.Contains("Greet and capture intent", rendered);
        Assert.Contains("Exit when: Caller stated intent", rendered);
    }

    [Fact]
    public void RenderRealtimePrompt_IncludesInstructionsAndExamples()
    {
        var workflow = Compile();
        var rendered = StagePromptRenderer.RenderRealtimePrompt(workflow, workflow.InitialStage);

        Assert.Contains("## Instructions", rendered);
        Assert.Contains("- Greet warmly.", rendered);
        Assert.Contains("## Example utterances", rendered);
        Assert.Contains("Welcome to ACME Bank.", rendered);
    }

    [Fact]
    public void RenderRealtimePrompt_IncludesAvailableTransitionsWithLabels()
    {
        var workflow = Compile();
        var rendered = StagePromptRenderer.RenderRealtimePrompt(workflow, workflow.InitialStage);

        Assert.Contains("## Available transitions", rendered);
        Assert.Contains("`balance`", rendered);
        Assert.Contains("Caller wants balance.", rendered);
    }

    [Fact]
    public void RenderRealtimePrompt_OmitsTransitionsSectionOnTerminalStages()
    {
        var workflow = Compile();
        var rendered = StagePromptRenderer.RenderRealtimePrompt(workflow, workflow.GetStage("next"));

        Assert.DoesNotContain("## Available transitions", rendered);
    }

    [Fact]
    public void RenderRealtimePrompt_IncludesCollectedState()
    {
        var workflow = Compile();
        var state = new IvrWorkflowState();
        state.Set("caller_name", "Jordan");

        var rendered = StagePromptRenderer.RenderRealtimePrompt(workflow, workflow.InitialStage, state);

        Assert.Contains("## Collected information", rendered);
        Assert.Contains("caller_name: Jordan", rendered);
    }
}
