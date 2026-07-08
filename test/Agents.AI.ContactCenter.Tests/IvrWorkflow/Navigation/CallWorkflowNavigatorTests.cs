using global::Agents.AI.ContactCenter.Authentication;
using global::Agents.AI.ContactCenter.IvrWorkflow;
using global::Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using global::Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using global::Agents.AI.ContactCenter.IvrWorkflow.Navigation;
using Microsoft.Extensions.DependencyInjection;

// Disambiguate from the legacy IvrWorkflow.TransitionEvaluation; remove this alias once
// the legacy navigator is deleted in a later phase.
using TransitionEvaluation = global::Agents.AI.ContactCenter.IvrWorkflow.Navigation.TransitionEvaluation;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Navigation;

public sealed class CallWorkflowNavigatorTests
{
    private static CompiledCallWorkflow Compile() => new WorkflowGraphCompiler().Compile(new WorkflowBlueprint
    {
        Id = "demo",
        InitialStageId = "welcome",
        Stages =
        [
            new StageBlueprint
            {
                Id = "welcome",
                Goal = "Greet and collect intent.",
                Transitions =
                [
                    new TransitionBlueprint
                    {
                        TargetStageId = "balance",
                        Label = "balance",
                        Requires = [PredicateRef.AuthVerificationLevel(CallerVerificationLevel.MultiFactor)],
                        OnBlockedStageId = "verify",
                    },
                    new TransitionBlueprint
                    {
                        TargetStageId = "transfer",
                        Label = "agent",
                    },
                ],
            },
            new StageBlueprint
            {
                Id = "verify",
                Transitions = [new TransitionBlueprint { TargetStageId = "balance", Label = "verified" }],
            },
            new StageBlueprint { Id = "balance", Terminal = true, TerminalOutcome = BlueprintTerminalOutcome.Success },
            new StageBlueprint { Id = "transfer", Terminal = true, TerminalOutcome = BlueprintTerminalOutcome.Escalated },
        ],
    });

    private static IServiceProvider EmptyServices() => new ServiceCollection().BuildServiceProvider();

    private static IServiceProvider ServicesWithAuth(CallerVerificationLevel level)
    {
        var auth = new CallerAuthenticationState();
        auth.TryPromote(CallerIdentity.Anonymous with { UserId = "u", VerificationLevel = level });
        return new ServiceCollection().AddSingleton(auth).BuildServiceProvider();
    }

    [Fact]
    public void EnterInitialStage_PositionsAtInitial()
    {
        var workflow = Compile();
        var nav = new CallWorkflowNavigator(workflow, new IvrWorkflowState(), EmptyServices());

        var stage = nav.EnterInitialStage();

        Assert.Equal("welcome", stage.Id);
        Assert.Same(stage, nav.CurrentStage);
        Assert.Equal("welcome", nav.State.CurrentStepName);
        Assert.Equal(IvrWorkflowStatus.Running, nav.State.Status);
    }

    [Fact]
    public void EnterInitialStage_ResumesFromPriorState()
    {
        var workflow = Compile();
        var state = new IvrWorkflowState { CurrentStepName = "verify" };
        var nav = new CallWorkflowNavigator(workflow, state, EmptyServices());

        var stage = nav.EnterInitialStage();

        Assert.Equal("verify", stage.Id);
    }

    [Fact]
    public async Task EvaluateTransition_Allowed_WhenPredicatePasses()
    {
        var workflow = Compile();
        var nav = new CallWorkflowNavigator(workflow, new IvrWorkflowState(), ServicesWithAuth(CallerVerificationLevel.MultiFactor));
        nav.EnterInitialStage();

        var result = await nav.EvaluateTransitionAsync("balance");

        var allowed = Assert.IsType<TransitionEvaluation.Allowed>(result);
        Assert.Equal("balance", allowed.Edge.TargetStageId);
    }

    [Fact]
    public async Task EvaluateTransition_BlockedRoutedTo_WhenOnBlockedDeclared()
    {
        var workflow = Compile();
        var nav = new CallWorkflowNavigator(workflow, new IvrWorkflowState(), EmptyServices()); // no auth state
        nav.EnterInitialStage();

        var result = await nav.EvaluateTransitionAsync("balance");

        var blocked = Assert.IsType<TransitionEvaluation.BlockedRoutedTo>(result);
        Assert.Equal("balance", blocked.Edge.TargetStageId);
        Assert.Equal("verify", blocked.FallbackEdge.TargetStageId);
        Assert.False(string.IsNullOrEmpty(blocked.Reason));
    }

    [Fact]
    public async Task EvaluateTransition_Blocked_WhenNoFallback()
    {
        var workflow = new WorkflowGraphCompiler().Compile(new WorkflowBlueprint
        {
            Id = "no-fallback",
            InitialStageId = "a",
            Stages =
            [
                new StageBlueprint
                {
                    Id = "a",
                    Transitions =
                    [
                        new TransitionBlueprint
                        {
                            TargetStageId = "b",
                            Requires = [PredicateRef.AuthVerificationLevel(CallerVerificationLevel.MultiFactor)],
                        },
                    ],
                },
                new StageBlueprint { Id = "b", Terminal = true },
            ],
        });

        var nav = new CallWorkflowNavigator(workflow, new IvrWorkflowState(), EmptyServices());
        nav.EnterInitialStage();

        var result = await nav.EvaluateTransitionAsync("b");

        Assert.IsType<TransitionEvaluation.Blocked>(result);
    }

    [Fact]
    public async Task EvaluateTransition_Invalid_WhenNoEdge()
    {
        var workflow = Compile();
        var nav = new CallWorkflowNavigator(workflow, new IvrWorkflowState(), EmptyServices());
        nav.EnterInitialStage();

        var result = await nav.EvaluateTransitionAsync("nonexistent");

        var invalid = Assert.IsType<TransitionEvaluation.Invalid>(result);
        Assert.Contains("nonexistent", invalid.Reason);
    }

    [Fact]
    public async Task ApplyTransition_AdvancesCurrentStageAndState()
    {
        var workflow = Compile();
        var nav = new CallWorkflowNavigator(workflow, new IvrWorkflowState(), EmptyServices());
        nav.EnterInitialStage();

        var evaluation = await nav.EvaluateTransitionAsync("transfer");
        var allowed = Assert.IsType<TransitionEvaluation.Allowed>(evaluation);

        var newStage = nav.ApplyTransition(allowed.Edge);

        Assert.Equal("transfer", newStage.Id);
        Assert.Equal("transfer", nav.State.CurrentStepName);
        Assert.True(nav.IsComplete);
        Assert.Equal(IvrWorkflowStatus.Completed, nav.State.Status);
    }

    [Fact]
    public async Task ApplyTransition_ToBlockedFallback_ProgressesToFallback()
    {
        var workflow = Compile();
        var nav = new CallWorkflowNavigator(workflow, new IvrWorkflowState(), EmptyServices());
        nav.EnterInitialStage();

        var evaluation = await nav.EvaluateTransitionAsync("balance");
        var blocked = Assert.IsType<TransitionEvaluation.BlockedRoutedTo>(evaluation);

        var newStage = nav.ApplyTransition(blocked.FallbackEdge);

        Assert.Equal("verify", newStage.Id);
    }

    [Fact]
    public async Task EvaluateTransition_BeforeEnterInitial_Throws()
    {
        var nav = new CallWorkflowNavigator(Compile(), new IvrWorkflowState(), EmptyServices());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await nav.EvaluateTransitionAsync("balance"));
    }

    [Fact]
    public void TierSwap_StatePreserved_NavigatorResumesAtCorrectStage()
    {
        var workflow = Compile();
        var state = new IvrWorkflowState();
        state.Set("intent", "balance");

        var nav1 = new CallWorkflowNavigator(workflow, state, EmptyServices());
        nav1.EnterInitialStage();
        nav1.ApplyTransition(nav1.CurrentStage!.FindEdgeTo("transfer")!);

        // Simulate tier swap: new navigator instance, same state.
        var nav2 = new CallWorkflowNavigator(workflow, state, EmptyServices());
        var resumed = nav2.EnterInitialStage();

        Assert.Equal("transfer", resumed.Id);
        Assert.Equal("balance", state.Get<string>("intent"));
    }
}
