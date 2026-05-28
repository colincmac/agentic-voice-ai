using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Registry;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow;

public class IvrAdvanceToolInvokerTests
{
    [Fact]
    public async Task InvokeAsync_NoCurrentStep_ReturnsNoCurrentStepAndSkipsApply()
    {
        var navigator = BuildNavigator(BuildWorkflow());
        // Do NOT call EnterInitialStep — leaves CurrentStep null.
        var applyCalled = 0;
        var invoker = new IvrAdvanceToolInvoker(
            navigator,
            (_, _) => { applyCalled++; return Task.CompletedTask; });

        var result = await invoker.InvokeAsync("next", TestContext.Current.CancellationToken);

        Assert.Equal(AdvanceToolResult.StatusNoCurrentStep, result.Status);
        Assert.Equal(0, applyCalled);
    }

    [Fact]
    public async Task InvokeAsync_UnknownChoice_ReturnsUnknownChoiceWithAllowedTargets()
    {
        var navigator = BuildNavigator(BuildWorkflow());
        navigator.EnterInitialStep();
        var applyCalled = 0;
        var invoker = new IvrAdvanceToolInvoker(
            navigator,
            (_, _) => { applyCalled++; return Task.CompletedTask; });

        var result = await invoker.InvokeAsync("not_a_real_target", TestContext.Current.CancellationToken);

        Assert.Equal(AdvanceToolResult.StatusUnknownChoice, result.Status);
        Assert.Equal("verify", result.From);
        Assert.NotNull(result.AllowedTargets);
        Assert.Contains("next", result.AllowedTargets!);
        Assert.Equal(0, applyCalled);
    }

    [Fact]
    public async Task InvokeAsync_EmptyChoice_ReturnsUnknownChoice()
    {
        var navigator = BuildNavigator(BuildWorkflow());
        navigator.EnterInitialStep();
        var invoker = new IvrAdvanceToolInvoker(navigator, (_, _) => Task.CompletedTask);

        var result = await invoker.InvokeAsync("   ", TestContext.Current.CancellationToken);

        Assert.Equal(AdvanceToolResult.StatusUnknownChoice, result.Status);
        Assert.NotNull(result.AllowedTargets);
    }

    [Fact]
    public async Task InvokeAsync_StageChoice_TransitionsAndInvokesApplyStage()
    {
        var navigator = BuildNavigator(BuildWorkflow());
        navigator.EnterInitialStep();

        RealtimeIvrWorkflowStep? applied = null;
        var invoker = new IvrAdvanceToolInvoker(
            navigator,
            (step, _) => { applied = step; return Task.CompletedTask; });

        var result = await invoker.InvokeAsync("next", TestContext.Current.CancellationToken);

        Assert.Equal(AdvanceToolResult.StatusAdvanced, result.Status);
        Assert.Equal("verify", result.From);
        Assert.Equal("next", result.To);
        Assert.False(result.Terminal);
        Assert.NotNull(applied);
        Assert.Equal("next", applied!.Id);
        Assert.Equal("next", navigator.CurrentStep?.Id);
    }

    [Fact]
    public async Task InvokeAsync_IntentChoice_ResolvesToIntentTargetAndInvokesApplyStage()
    {
        var workflow = BuildWorkflow(intents: new Dictionary<string, RealtimeIvrWorkflowIntent>(StringComparer.OrdinalIgnoreCase)
        {
            ["balance"] = new RealtimeIvrWorkflowIntent("balance", Examples: [], NextStepId: "next"),
        });
        var navigator = BuildNavigator(workflow);
        navigator.EnterInitialStep();

        RealtimeIvrWorkflowStep? applied = null;
        var invoker = new IvrAdvanceToolInvoker(
            navigator,
            (step, _) => { applied = step; return Task.CompletedTask; });

        var result = await invoker.InvokeAsync("balance", TestContext.Current.CancellationToken);

        Assert.Equal(AdvanceToolResult.StatusAdvanced, result.Status);
        Assert.Equal("next", result.To);
        Assert.NotNull(applied);
    }

    [Fact]
    public async Task InvokeAsync_IntentWithoutTransition_ReturnsKindAndSkipsApply()
    {
        var workflow = BuildWorkflow(intents: new Dictionary<string, RealtimeIvrWorkflowIntent>(StringComparer.OrdinalIgnoreCase)
        {
            ["help"] = new RealtimeIvrWorkflowIntent("help", Examples: []),
        });
        var navigator = BuildNavigator(workflow);
        navigator.EnterInitialStep();

        var applyCalled = 0;
        var invoker = new IvrAdvanceToolInvoker(
            navigator,
            (_, _) => { applyCalled++; return Task.CompletedTask; });

        var result = await invoker.InvokeAsync("help", TestContext.Current.CancellationToken);

        Assert.Equal(AdvanceToolResult.StatusIntentWithoutTransition, result.Status);
        Assert.Equal(0, applyCalled);
        // Navigator did not transition.
        Assert.Equal("verify", navigator.CurrentStep?.Id);
    }

    [Fact]
    public async Task InvokeAsync_TerminalTarget_ReturnsAdvancedTerminal()
    {
        var navigator = BuildNavigator(BuildWorkflow(terminalNext: true));
        navigator.EnterInitialStep();

        RealtimeIvrWorkflowStep? applied = null;
        var invoker = new IvrAdvanceToolInvoker(
            navigator,
            (step, _) => { applied = step; return Task.CompletedTask; });

        var result = await invoker.InvokeAsync("next", TestContext.Current.CancellationToken);

        Assert.Equal(AdvanceToolResult.StatusAdvancedTerminal, result.Status);
        Assert.True(result.Terminal);
        Assert.NotNull(applied);
        Assert.True(applied!.Terminal);
    }

    private static IvrWorkflowNavigator BuildNavigator(RealtimeIvrWorkflowDefinition workflow)
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        return new IvrWorkflowNavigator(
            workflow,
            new IvrWorkflowState { Status = IvrWorkflowStatus.Running },
            sp);
    }

    private static RealtimeIvrWorkflowDefinition BuildWorkflow(
        IReadOnlyDictionary<string, RealtimeIvrWorkflowIntent>? intents = null,
        bool terminalNext = false) => new()
        {
            Name = "advance-invoker-test-ivr",
            BasePrompt = new RealtimePrompt(),
            Steps =
            [
                new RealtimeIvrWorkflowStep
                {
                    Id = "verify",
                    ConversationState = new ConversationState
                    {
                        Id = "verify",
                        Description = "Verify the caller",
                        Instructions = ["Ask the caller why they are calling."],
                        Transitions = [new StateTransition { NextStep = "next", Condition = "ready" }]
                    },
                    Intents = intents ?? new Dictionary<string, RealtimeIvrWorkflowIntent>(StringComparer.OrdinalIgnoreCase)
                },
                new RealtimeIvrWorkflowStep
                {
                    Id = "next",
                    ConversationState = new ConversationState
                    {
                        Id = "next",
                        Description = "Wrap up",
                        Instructions = ["Say goodbye."]
                    },
                    Terminal = terminalNext
                }
            ]
        };
}
