using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Registry;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Registry;

/// <summary>
/// Covers <see cref="IvrAdvanceFunctions"/> — both the per-target function builder
/// (<see cref="IvrAdvanceFunctions.BuildForStep"/>) and the runtime advance pipeline
/// invoked through the synthesized functions' bodies.
/// </summary>
public class IvrAdvanceFunctionsTests
{
    [Fact]
    public void FunctionNameFor_prefixes_and_passes_through_safe_ids()
    {
        Assert.Equal("advance_to_make-payment", IvrAdvanceFunctions.FunctionNameFor("make-payment"));
        Assert.Equal("advance_to_balance_inquiry", IvrAdvanceFunctions.FunctionNameFor("balance_inquiry"));
    }

    [Fact]
    public void FunctionNameFor_sanitizes_illegal_characters()
    {
        Assert.Equal("advance_to_pay_now_", IvrAdvanceFunctions.FunctionNameFor("pay now!"));
    }

    [Fact]
    public void IsAdvanceFunctionName_matches_prefix_only()
    {
        Assert.True(IvrAdvanceFunctions.IsAdvanceFunctionName("advance_to_confirm"));
        Assert.False(IvrAdvanceFunctions.IsAdvanceFunctionName("confirm"));
        Assert.False(IvrAdvanceFunctions.IsAdvanceFunctionName(null));
        Assert.False(IvrAdvanceFunctions.IsAdvanceFunctionName(string.Empty));
    }

    [Fact]
    public void BuildForStep_returns_empty_for_terminal_step()
    {
        var workflow = BuildWorkflow();
        var navigator = BuildNavigator(workflow);
        var functions = new IvrAdvanceFunctions(navigator, (_, _) => Task.CompletedTask);

        var terminal = workflow.Steps.Single(s => s.Id == "done");

        Assert.Empty(functions.BuildForStep(terminal));
    }

    [Fact]
    public void BuildForStep_emits_one_function_per_unique_target()
    {
        var workflow = BuildWorkflow(intents: new Dictionary<string, RealtimeIvrWorkflowIntent>(StringComparer.OrdinalIgnoreCase)
        {
            ["balance"] = new RealtimeIvrWorkflowIntent("balance", Examples: [], NextStepId: "next"),
        });
        var navigator = BuildNavigator(workflow);
        var functions = new IvrAdvanceFunctions(navigator, (_, _) => Task.CompletedTask);

        var verify = workflow.Steps.Single(s => s.Id == "verify");
        var built = functions.BuildForStep(verify).ToList();

        // Intent and raw transition both target "next" — should dedupe to one function.
        Assert.Single(built);
        Assert.Equal("advance_to_next", built[0].Name);
        Assert.Contains("balance", built[0].Description);
    }

    [Fact]
    public void BuildForStep_skips_intents_without_next_step_id()
    {
        var workflow = BuildWorkflow(intents: new Dictionary<string, RealtimeIvrWorkflowIntent>(StringComparer.OrdinalIgnoreCase)
        {
            ["help"] = new RealtimeIvrWorkflowIntent("help", Examples: [], CapabilityId: "help-capability"),
        });
        var navigator = BuildNavigator(workflow);
        var functions = new IvrAdvanceFunctions(navigator, (_, _) => Task.CompletedTask);

        var verify = workflow.Steps.Single(s => s.Id == "verify");
        var built = functions.BuildForStep(verify).ToList();

        // Only the raw transition target ("next") survives — the help intent has no
        // NextStepId so it should *not* be exposed as an advance function.
        Assert.Single(built);
        Assert.Equal("advance_to_next", built[0].Name);
    }

    [Fact]
    public async Task Advance_function_with_no_current_step_returns_no_current_step()
    {
        var workflow = BuildWorkflow();
        var navigator = BuildNavigator(workflow);
        // Don't call EnterInitialStep, but build functions for the initial step directly.
        var verify = workflow.Steps.Single(s => s.Id == "verify");
        var functions = new IvrAdvanceFunctions(navigator, (_, _) => Task.CompletedTask);

        var advance = functions.BuildForStep(verify).Single();
        var result = await advance.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);

        var json = Assert.IsType<System.Text.Json.JsonElement>(result);
        Assert.Equal(AdvanceToolResult.StatusNoCurrentStep, json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Advance_function_transitions_navigator_and_invokes_apply_callback()
    {
        var workflow = BuildWorkflow();
        var navigator = BuildNavigator(workflow);
        navigator.EnterInitialStep();

        RealtimeIvrWorkflowStep? applied = null;
        var functions = new IvrAdvanceFunctions(
            navigator,
            (step, _) => { applied = step; return Task.CompletedTask; });

        var advance = functions
            .BuildForStep(workflow.Steps.Single(s => s.Id == "verify"))
            .Single(f => f.Name == "advance_to_next");

        var result = await advance.InvokeAsync(
            new AIFunctionArguments { ["reason"] = "caller is ready" },
            TestContext.Current.CancellationToken);

        var json = Assert.IsType<System.Text.Json.JsonElement>(result);
        Assert.Equal(AdvanceToolResult.StatusAdvanced, json.GetProperty("status").GetString());
        Assert.Equal("verify", json.GetProperty("from").GetString());
        Assert.Equal("next", json.GetProperty("to").GetString());
        Assert.False(json.GetProperty("terminal").GetBoolean());
        Assert.Equal("caller is ready", json.GetProperty("reason").GetString());

        Assert.NotNull(applied);
        Assert.Equal("next", applied!.Id);
        Assert.Equal("next", navigator.CurrentStep?.Id);
    }

    [Fact]
    public async Task Advance_function_to_terminal_stage_returns_advanced_terminal()
    {
        var workflow = BuildWorkflow(terminalNext: true);
        var navigator = BuildNavigator(workflow);
        navigator.EnterInitialStep();

        RealtimeIvrWorkflowStep? applied = null;
        var functions = new IvrAdvanceFunctions(
            navigator,
            (step, _) => { applied = step; return Task.CompletedTask; });

        var advance = functions
            .BuildForStep(workflow.Steps.Single(s => s.Id == "verify"))
            .Single(f => f.Name == "advance_to_next");

        var result = await advance.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);

        var json = Assert.IsType<System.Text.Json.JsonElement>(result);
        Assert.Equal(AdvanceToolResult.StatusAdvancedTerminal, json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("terminal").GetBoolean());
        Assert.NotNull(applied);
        Assert.True(applied!.Terminal);
    }

    [Fact]
    public async Task Advance_function_resolved_through_intent_preserves_intent_routing()
    {
        var workflow = BuildWorkflow(intents: new Dictionary<string, RealtimeIvrWorkflowIntent>(StringComparer.OrdinalIgnoreCase)
        {
            ["balance"] = new RealtimeIvrWorkflowIntent("balance", Examples: [], NextStepId: "next"),
        });
        var navigator = BuildNavigator(workflow);
        navigator.EnterInitialStep();

        RealtimeIvrWorkflowStep? applied = null;
        var functions = new IvrAdvanceFunctions(
            navigator,
            (step, _) => { applied = step; return Task.CompletedTask; });

        // Even though "balance" intent shares the same target as the raw transition,
        // BuildForStep produces a single function — exercise it.
        var advance = functions.BuildForStep(workflow.Steps.Single(s => s.Id == "verify")).Single();
        Assert.Equal("advance_to_next", advance.Name);

        var result = await advance.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);

        var json = Assert.IsType<System.Text.Json.JsonElement>(result);
        Assert.Equal(AdvanceToolResult.StatusAdvanced, json.GetProperty("status").GetString());
        Assert.Equal("next", navigator.CurrentStep?.Id);
        Assert.NotNull(applied);
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
            Name = "advance-functions-test-ivr",
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
                },
                new RealtimeIvrWorkflowStep
                {
                    Id = "done",
                    Terminal = true,
                    ConversationState = new ConversationState
                    {
                        Id = "done",
                        Description = "Done",
                        Instructions = ["Done."]
                    }
                }
            ]
        };
}
