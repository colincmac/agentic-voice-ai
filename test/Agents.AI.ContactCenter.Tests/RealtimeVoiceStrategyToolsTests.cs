using System.Threading.Channels;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Registry;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Agents.AI.ContactCenter.Calling;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Agents.AI.ContactCenter.Calling.Strategies.RealtimeVoice;

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

        await using var strategy = new RealtimeVoiceStrategy(backend, workflow, TestTelemetry.LoggerFactory, TestTelemetry.Calling);

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

        await using var strategy = new RealtimeVoiceStrategy(backend, workflow, TestTelemetry.LoggerFactory, TestTelemetry.Calling);

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

        await using var strategy = new RealtimeVoiceStrategy(backend, workflow, TestTelemetry.LoggerFactory, TestTelemetry.Calling);

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

    [Fact]
    public async Task StartAsync_includes_advance_tool_when_step_has_transitions()
    {
        var tool = AIFunctionFactory.Create((Func<object?>)(() => null), "do_thing");

        var workflow = BuildWorkflowWithTransitions(
            initialStepId: "menu",
            initialTools: [tool],
            transitions: ["confirm"],
            otherStepIds: ["confirm"]);

        var backend = new ControllableRealtimeBackend("agent-1", "Agent 1");

        await using var strategy = new RealtimeVoiceStrategy(backend, workflow, TestTelemetry.LoggerFactory, TestTelemetry.Calling);

        await strategy.StartAsync(BuildStartContext());

        Assert.Single(backend.ToolUpdates);
        var pushed = backend.ToolUpdates[0];
        Assert.Equal(2, pushed.Count);
        Assert.Contains(pushed, t => t.Name == IvrAdvanceTool.AdvanceToolName);

        await strategy.StopAsync();
    }

    [Fact]
    public async Task Advance_tool_invocation_transitions_navigator_and_refreshes_stage()
    {
        var initialTool = AIFunctionFactory.Create((Func<object?>)(() => null), "menu_tool");
        var confirmTool = AIFunctionFactory.Create((Func<object?>)(() => null), "confirm_tool");

        var workflow = new RealtimeIvrWorkflowDefinition
        {
            Name = "advance-test",
            BasePrompt = new RealtimePrompt(),
            Steps =
            [
                new RealtimeIvrWorkflowStep
                {
                    Id = "menu",
                    AvailableTools = [initialTool],
                    ConversationState = new ConversationState
                    {
                        Id = "menu",
                        Description = "menu",
                        Goal = "menu",
                        Instructions = ["pick"],
                        Transitions = [new StateTransition { NextStep = "confirm", Condition = "default" }],
                    },
                },
                new RealtimeIvrWorkflowStep
                {
                    Id = "confirm",
                    AvailableTools = [confirmTool],
                    Terminal = true,
                    ConversationState = new ConversationState
                    {
                        Id = "confirm",
                        Description = "confirm",
                        Goal = "confirm",
                        Instructions = ["finalize"],
                    },
                },
            ]
        };

        var backend = new ControllableRealtimeBackend("agent-adv", "Adv Agent");
        await using var strategy = new RealtimeVoiceStrategy(backend, workflow, TestTelemetry.LoggerFactory, TestTelemetry.Calling);

        await strategy.StartAsync(BuildStartContext());
        Assert.True(await backend.WaitForConnectAsync(TimeSpan.FromSeconds(2)));

        // Drain the initial events (initial WorkflowStepEntered + AgentSpeakingChanged) so the
        // assertions below observe only the transition-driven events.
        await DrainEventsAsync(strategy, expected: 2);

        // The advance tool now runs inline under UseFunctionInvocation() — invoke it directly
        // on the pushed tool surface to exercise the IvrAdvanceToolInvoker path.
        var pushed = backend.ToolUpdates[0];
        var advance = Assert.IsAssignableFrom<AIFunction>(
            pushed.Single(t => t.Name == IvrAdvanceTool.AdvanceToolName));

        var raw = await advance.InvokeAsync(
            new AIFunctionArguments { ["next_stage"] = "confirm" },
            TestContext.Current.CancellationToken);

        // AIFunctionFactory serializes the returned AdvanceToolResult through JSON so the
        // realtime model gets a structured payload it can read. Inspect the JsonElement.
        var json = Assert.IsType<System.Text.Json.JsonElement>(raw);
        Assert.Equal(AdvanceToolResult.StatusAdvancedTerminal, json.GetProperty("status").GetString());
        Assert.Equal("confirm", json.GetProperty("to").GetString());
        Assert.True(json.GetProperty("terminal").GetBoolean());

        // Wait for the apply-stage callback to push the new tools / emit transition events.
        var events = await DrainEventsAsync(strategy, expected: 2, timeoutMs: 2000);
        Assert.Contains(events, e => e is StrategyEvent.WorkflowStepEntered w && w.StepId == "confirm");

        Assert.Equal("confirm", strategy.WorkflowState.CurrentStepName);
        Assert.True(backend.ToolUpdates.Count >= 2, $"expected >= 2 tool updates, got {backend.ToolUpdates.Count}");
        var lastTools = backend.ToolUpdates[^1];
        Assert.DoesNotContain(lastTools, t => t.Name == IvrAdvanceTool.AdvanceToolName);

        await strategy.StopAsync();
    }

    private static async Task<List<StrategyEvent>> DrainEventsAsync(
        RealtimeVoiceStrategy strategy,
        int expected,
        int timeoutMs = 1000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        var collected = new List<StrategyEvent>();
        while (collected.Count < expected && DateTimeOffset.UtcNow < deadline)
        {
            using var readCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                var ev = await strategy.Events.ReadAsync(readCts.Token);
                collected.Add(ev);
            }
            catch (OperationCanceledException)
            {
                // continue polling until the deadline
            }
        }
        return collected;
    }

    private static RealtimeIvrWorkflowDefinition BuildWorkflowWithTransitions(
        string initialStepId,
        IReadOnlyList<AITool>? initialTools,
        string[] transitions,
        string[] otherStepIds)
    {
        var steps = new List<RealtimeIvrWorkflowStep>
        {
            new RealtimeIvrWorkflowStep
            {
                Id = initialStepId,
                AvailableTools = initialTools,
                ConversationState = new ConversationState
                {
                    Id = initialStepId,
                    Description = $"step {initialStepId}",
                    Goal = "test",
                    Instructions = ["do the thing"],
                    Transitions = transitions.Length == 0
                        ? null
                        : [.. transitions.Select(t => new StateTransition { NextStep = t, Condition = "default" })],
                },
            },
        };
        foreach (var s in otherStepIds)
        {
            steps.Add(new RealtimeIvrWorkflowStep
            {
                Id = s,
                Terminal = true,
                ConversationState = new ConversationState
                {
                    Id = s,
                    Description = s,
                    Goal = s,
                    Instructions = ["done"],
                },
            });
        }
        return new RealtimeIvrWorkflowDefinition
        {
            Name = "tools-test-ivr-transitions",
            BasePrompt = new RealtimePrompt(),
            Steps = steps,
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
