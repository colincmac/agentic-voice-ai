using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.Extensions.Tests.IvrWorkflow;

public class IvrWorkflowNavigatorGuardsTests
{
    [Fact]
    public async Task InvokeActionAsync_GuardFails_ReturnsRejectAndSkipsTool()
    {
        var calls = 0;
        var tool = AIFunctionFactory.Create((Func<object?>)(() => { calls++; return null; }), "do_thing");

        var workflow = BuildWorkflow(stepId: "verify", tools: [tool], guards: [
            new RequiredStateGuard("pin", "PIN must be verified")
        ]);

        var navigator = BuildNavigator(workflow);
        navigator.EnterInitialStep();

        var failurePrompt = "Please verify your PIN first";
        var failureUri = new Uri("https://example.com/failure.wav");

        var result = await navigator.InvokeActionAsync(
            tool,
            boundArguments: null,
            extraArguments: null,
            successNextStepId: "next",
            failurePrompt: failurePrompt,
            failureAudio: failureUri,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, calls);
        var reject = Assert.IsType<DtmfActionResult.Reject>(result);
        Assert.Equal(failurePrompt, reject.ErrorPrompt);
        Assert.Equal(failureUri, reject.ErrorAudioFile);
    }

    [Fact]
    public async Task InvokeActionAsync_GuardsPass_InvokesToolAndInterpretsResult()
    {
        var calls = 0;
        var tool = AIFunctionFactory.Create((Func<object?>)(() => { calls++; return null; }), "do_thing");

        var workflow = BuildWorkflow(stepId: "verify", tools: [tool], guards: [
            new RequiredStateGuard("pin")
        ]);

        var navigator = BuildNavigator(workflow);
        navigator.EnterInitialStep();
        navigator.State.Set("pin", "1234");

        var result = await navigator.InvokeActionAsync(
            tool,
            boundArguments: null,
            extraArguments: null,
            successNextStepId: "next",
            failurePrompt: "blocked",
            failureAudio: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
        var transition = Assert.IsType<DtmfActionResult.Transition>(result);
        Assert.Equal("next", transition.NextStepId);
    }

    [Fact]
    public async Task InvokeActionAsync_NoGuards_InvokesToolNormally()
    {
        var calls = 0;
        var tool = AIFunctionFactory.Create((Func<object?>)(() => { calls++; return null; }), "do_thing");

        var workflow = BuildWorkflow(stepId: "verify", tools: [tool], guards: []);

        var navigator = BuildNavigator(workflow);
        navigator.EnterInitialStep();

        var result = await navigator.InvokeActionAsync(
            tool,
            boundArguments: null,
            extraArguments: null,
            successNextStepId: null,
            failurePrompt: null,
            failureAudio: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
        Assert.IsType<DtmfActionResult.Repeat>(result);
    }

    [Fact]
    public async Task WrapToolsWithCurrentGuards_NoCurrentStep_ReturnsInputUnchanged()
    {
        var tool = AIFunctionFactory.Create(() => "ok", "fn");
        var workflow = BuildWorkflow(stepId: "verify", tools: [tool], guards: [
            new RequiredStateGuard("pin")
        ]);

        var navigator = BuildNavigator(workflow);
        // Do NOT call EnterInitialStep — CurrentStep stays null.

        var input = new AITool[] { tool };
        var wrapped = navigator.WrapToolsWithCurrentGuards(input).ToList();

        Assert.Single(wrapped);
        Assert.Same(tool, wrapped[0]);

        // And it actually runs unguarded.
        var raw = await ((AIFunction)wrapped[0]).InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);
        Assert.Equal("ok", raw?.ToString());
    }

    [Fact]
    public async Task WrapToolsWithCurrentGuards_StepHasGuards_WrapsAIFunctionsAndPassesThroughOthers()
    {
        var fnCalls = 0;
        var fn = AIFunctionFactory.Create(() => { fnCalls++; return "ok"; }, "fn");
        var nonFn = new NonInvocableTool();

        var workflow = BuildWorkflow(stepId: "verify", tools: [fn], guards: [
            new RequiredStateGuard("pin", "PIN missing")
        ]);

        var navigator = BuildNavigator(workflow);
        navigator.EnterInitialStep();

        var wrapped = navigator.WrapToolsWithCurrentGuards([fn, nonFn]).ToList();

        Assert.IsType<GuardedAIFunction>(wrapped[0]);
        Assert.Same(nonFn, wrapped[1]);

        var blocked = await ((AIFunction)wrapped[0]).InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, fnCalls);
        Assert.Equal("Action blocked: PIN missing", blocked?.ToString());
    }

    [Fact]
    public async Task WrapToolsWithCurrentGuards_WrapperReadsLiveStateAcrossInvocations()
    {
        var fnCalls = 0;
        var fn = AIFunctionFactory.Create(() => { fnCalls++; return "ran"; }, "fn");

        var workflow = BuildWorkflow(stepId: "verify", tools: [fn], guards: [
            new RequiredStateGuard("pin")
        ]);

        var navigator = BuildNavigator(workflow);
        navigator.EnterInitialStep();

        var guarded = (AIFunction)navigator.WrapToolsWithCurrentGuards([fn]).First();

        var first = await guarded.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);
        Assert.StartsWith("Action blocked", first?.ToString());
        Assert.Equal(0, fnCalls);

        navigator.State.Set("pin", "1234");

        var second = await guarded.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);
        Assert.Equal("ran", second?.ToString());
        Assert.Equal(1, fnCalls);
    }

    [Fact]
    public async Task WrapToolsWithCurrentGuards_StepWithoutGuards_ReturnsInputUnchanged()
    {
        var fn = AIFunctionFactory.Create(() => "ok", "fn");
        var workflow = BuildWorkflow(stepId: "verify", tools: [fn], guards: []);

        var navigator = BuildNavigator(workflow);
        navigator.EnterInitialStep();

        var input = new AITool[] { fn };
        var wrapped = navigator.WrapToolsWithCurrentGuards(input);

        Assert.Same(input, wrapped);

        var raw = await ((AIFunction)wrapped.First()).InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);
        Assert.Equal("ok", raw?.ToString());
    }

    private static IvrWorkflowNavigator BuildNavigator(RealtimeIvrWorkflowDefinition workflow)
    {
        using var sp = new ServiceCollection().BuildServiceProvider();
        return new IvrWorkflowNavigator(
            workflow,
            new IvrWorkflowState { Status = IvrWorkflowStatus.Running },
            sp);
    }

    private static RealtimeIvrWorkflowDefinition BuildWorkflow(
        string stepId,
        IReadOnlyList<AITool> tools,
        IReadOnlyList<IIvrStepGuard> guards) => new()
        {
            Name = "guards-test-ivr",
            BasePrompt = new RealtimePrompt(),
            Steps =
            [
                new RealtimeIvrWorkflowStep
                {
                    Id = stepId,
                    ConversationState = new ConversationState
                    {
                        Id = stepId,
                        Description = "Verify the caller",
                        Goal = "Collect PIN",
                        Instructions = ["Ask for PIN"],
                        Transitions = [new StateTransition { NextStep = "next", Condition = "verified" }]
                    },
                    AvailableTools = tools,
                    Guards = guards
                },
                new RealtimeIvrWorkflowStep
                {
                    Id = "next",
                    ConversationState = new ConversationState
                    {
                        Id = "next",
                        Description = "Done",
                        Instructions = ["Wrap up"]
                    }
                }
            ]
        };

    private sealed class NonInvocableTool : AITool
    {
        public override string Name => "non_invocable";
        public override string Description => "non-invocable test tool";
    }
}
