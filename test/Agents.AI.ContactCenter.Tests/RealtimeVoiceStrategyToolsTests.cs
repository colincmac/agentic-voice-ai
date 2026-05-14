using System.Threading.Channels;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Implementation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.RealtimeVoice.Azure.Tests.Proposed;

/// <summary>
/// Verifies that <see cref="RealtimeVoiceStrategy"/> pushes the navigator's
/// guard-wrapped tool list into the backend at session start, and that the
/// wrappers gate invocation against the live workflow state.
/// </summary>
public class RealtimeVoiceStrategyToolsTests
{
    [Fact]
    public async Task StartAsync_pushes_guard_wrapped_tools_for_initial_step()
    {
        var calls = 0;
        var tool = AIFunctionFactory.Create((Func<object?>)(() => { calls++; return null; }), "do_thing");

        var workflow = BuildWorkflow(
            stepId: "verify",
            tools: [tool],
            guards: [new RequiredStateGuard("pin", "PIN must be verified")]);

        var backend = new ControllableRealtimeBackend("agent-1", "Agent 1");

        await using var strategy = new RealtimeVoiceStrategy(backend, workflow);

        await strategy.StartAsync(BuildStartContext());

        Assert.True(await backend.WaitForConnectAsync(TimeSpan.FromSeconds(2)));

        // Exactly one tool update was pushed (the initial step's tools).
        Assert.Single(backend.ToolUpdates);
        var pushed = backend.ToolUpdates[0];
        Assert.Single(pushed);

        // The pushed tool is a GuardedAIFunction wrapper, not the raw inner.
        var pushedFn = Assert.IsAssignableFrom<AIFunction>(pushed[0]);
        Assert.NotSame(tool, pushedFn);

        // Invoking without satisfying the guard returns the LLM-readable block
        // string and does NOT call the inner.
        var blocked = await pushedFn.InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, calls);
        Assert.Contains("blocked", blocked?.ToString(), StringComparison.OrdinalIgnoreCase);

        // Setting the guarded state lets the next invocation through.
        strategy.WorkflowState.Set("pin", "1234");
        _ = await pushedFn.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);

        await strategy.StopAsync();
    }

    [Fact]
    public async Task StartAsync_pushes_unwrapped_tools_when_step_has_no_guards()
    {
        var tool = AIFunctionFactory.Create((Func<object?>)(() => null), "do_thing");

        var workflow = BuildWorkflow(
            stepId: "open",
            tools: [tool],
            guards: []);

        var backend = new ControllableRealtimeBackend("agent-1", "Agent 1");

        await using var strategy = new RealtimeVoiceStrategy(backend, workflow);

        await strategy.StartAsync(BuildStartContext());

        Assert.Single(backend.ToolUpdates);
        var pushed = backend.ToolUpdates[0];
        Assert.Single(pushed);

        // No guards on the step -> WrapToolsWithCurrentGuards returns input unchanged.
        Assert.Same(tool, pushed[0]);

        await strategy.StopAsync();
    }

    [Fact]
    public async Task StartAsync_pushes_empty_tool_list_when_step_has_no_tools()
    {
        var workflow = BuildWorkflow(
            stepId: "talk-only",
            tools: null,
            guards: []);

        var backend = new ControllableRealtimeBackend("agent-1", "Agent 1");

        await using var strategy = new RealtimeVoiceStrategy(backend, workflow);

        await strategy.StartAsync(BuildStartContext());

        Assert.Single(backend.ToolUpdates);
        Assert.Empty(backend.ToolUpdates[0]);

        await strategy.StopAsync();
    }

    private static StrategyStartContext BuildStartContext()
    {
        return new StrategyStartContext
        {
            CallId = "call-tools-test",
            InboundAudio = Channel.CreateUnbounded<AudioFrame>().Reader,
            InboundDtmf = Channel.CreateUnbounded<DtmfTone>().Reader,
            Services = new ServiceCollection().BuildServiceProvider(),
        };
    }

    private static RealtimeIvrWorkflowDefinition BuildWorkflow(
        string stepId,
        IReadOnlyList<AITool>? tools,
        IReadOnlyList<IIvrStepGuard> guards)
    {
        return new RealtimeIvrWorkflowDefinition
        {
            Name = "tools-test-ivr",
            BasePrompt = new RealtimePrompt(),
            Steps =
            [
                new RealtimeIvrWorkflowStep
                {
                    Id = stepId,
                    AvailableTools = tools,
                    Guards = guards,
                    ConversationState = new ConversationState
                    {
                        Id = stepId,
                        Description = $"step {stepId}",
                        Goal = "test",
                        Instructions = ["do the thing"]
                    }
                }
            ]
        };
    }
}
