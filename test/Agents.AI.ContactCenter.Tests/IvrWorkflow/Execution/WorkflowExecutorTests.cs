using global::Agents.AI.ContactCenter.Authentication;
using global::Agents.AI.ContactCenter.IvrWorkflow;
using global::Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using global::Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using global::Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Execution;

public sealed class WorkflowExecutorTests
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
                Transitions =
                [
                    new TransitionBlueprint
                    {
                        TargetStageId = "balance",
                        Label = "balance",
                        Requires = [PredicateRef.AuthVerificationLevel(CallerVerificationLevel.MultiFactor)],
                        OnBlockedStageId = "verify",
                    },
                    new TransitionBlueprint { TargetStageId = "transfer", Label = "agent" },
                ],
            },
            new StageBlueprint
            {
                Id = "verify",
                Transitions = [new TransitionBlueprint { TargetStageId = "balance", Label = "verified" }],
            },
            new StageBlueprint { Id = "balance", Terminal = true },
            new StageBlueprint { Id = "transfer", Terminal = true },
        ],
    });

    private static (WorkflowExecutor Executor, List<string> Rendered) NewExecutor(
        CompiledCallWorkflow workflow,
        IServiceProvider? sp = null)
    {
        sp ??= new ServiceCollection().BuildServiceProvider();
        var session = new CallWorkflowSession(workflow, new IvrWorkflowState(), sp);
        var rendered = new List<string>();
        var executor = new WorkflowExecutor(session, (stage, _) =>
        {
            rendered.Add(stage.Id);
            return ValueTask.CompletedTask;
        });
        return (executor, rendered);
    }

    [Fact]
    public async Task EnterAsync_RendersInitialStage()
    {
        var workflow = Compile();
        var (executor, rendered) = NewExecutor(workflow);

        var stage = await executor.EnterAsync();

        Assert.Equal("welcome", stage.Id);
        Assert.Equal(["welcome"], rendered);
    }

    [Fact]
    public async Task AdvanceTo_Allowed_AdvancesAndRenders()
    {
        var workflow = Compile();
        var (executor, rendered) = NewExecutor(workflow);
        await executor.EnterAsync();
        rendered.Clear();

        var outcome = await executor.AdvanceToAsync("transfer");

        var advanced = Assert.IsType<AdvanceOutcome.Advanced>(outcome);
        Assert.Equal("transfer", advanced.NewStage.Id);
        Assert.Equal(["transfer"], rendered);
    }

    [Fact]
    public async Task AdvanceTo_BlockedWithFallback_RoutesAndRenders()
    {
        var workflow = Compile();
        var (executor, rendered) = NewExecutor(workflow);
        await executor.EnterAsync();
        rendered.Clear();

        var outcome = await executor.AdvanceToAsync("balance");

        var fb = Assert.IsType<AdvanceOutcome.AdvancedToFallback>(outcome);
        Assert.Equal("verify", fb.NewStage.Id);
        Assert.False(string.IsNullOrEmpty(fb.Reason));
        Assert.Equal(["verify"], rendered);
    }

    [Fact]
    public async Task AdvanceTo_BlockedNoFallback_DeniedWithoutRender()
    {
        var blueprint = new WorkflowBlueprint
        {
            Id = "no-fb",
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
        };
        var workflow = new WorkflowGraphCompiler().Compile(blueprint);
        var (executor, rendered) = NewExecutor(workflow);
        await executor.EnterAsync();
        rendered.Clear();

        var outcome = await executor.AdvanceToAsync("b");

        Assert.IsType<AdvanceOutcome.Denied>(outcome);
        Assert.Empty(rendered);
    }

    [Fact]
    public async Task AdvanceTo_InvalidTarget_InvalidWithoutRender()
    {
        var workflow = Compile();
        var (executor, rendered) = NewExecutor(workflow);
        await executor.EnterAsync();
        rendered.Clear();

        var outcome = await executor.AdvanceToAsync("nope");

        Assert.IsType<AdvanceOutcome.Invalid>(outcome);
        Assert.Empty(rendered);
    }

    [Fact]
    public async Task AdvanceTo_ConcurrentCalls_AreSerialized()
    {
        var workflow = new WorkflowGraphCompiler().Compile(new WorkflowBlueprint
        {
            Id = "race",
            InitialStageId = "a",
            Stages =
            [
                new StageBlueprint { Id = "a", Transitions = [new TransitionBlueprint { TargetStageId = "b", Label = "go" }] },
                new StageBlueprint { Id = "b", Transitions = [new TransitionBlueprint { TargetStageId = "c", Label = "go" }] },
                new StageBlueprint { Id = "c", Terminal = true },
            ],
        });

        var sp = new ServiceCollection().BuildServiceProvider();
        var session = new CallWorkflowSession(workflow, new IvrWorkflowState(), sp);
        var rendered = new List<string>();
        var renderStarted = new TaskCompletionSource();
        var releaseRender = new TaskCompletionSource();

        var executor = new WorkflowExecutor(session, async (stage, _) =>
        {
            rendered.Add(stage.Id);
            if (stage.Id == "b")
            {
                renderStarted.TrySetResult();
                await releaseRender.Task;
            }
        });

        await executor.EnterAsync();

        var first = executor.AdvanceToAsync("b").AsTask();
        await renderStarted.Task;

        // Second call must wait until first completes (render is blocked).
        var second = executor.AdvanceToAsync("c").AsTask();
        Assert.False(second.IsCompleted);

        releaseRender.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["a", "b", "c"], rendered);
    }

    [Fact]
    public async Task AdvanceAlong_Allowed_AdvancesAndRenders()
    {
        var workflow = Compile();
        var (executor, rendered) = NewExecutor(workflow);
        await executor.EnterAsync();
        rendered.Clear();

        var edge = executor.Navigator.CurrentStage!.FindEdgeByLabel("agent")!;
        var outcome = await executor.AdvanceAlongAsync(edge);

        var advanced = Assert.IsType<AdvanceOutcome.Advanced>(outcome);
        Assert.Equal("transfer", advanced.NewStage.Id);
        Assert.Equal(["transfer"], rendered);
    }

    [Fact]
    public async Task AdvanceAlong_BlockedWithFallback_RoutesAndRenders()
    {
        var workflow = Compile();
        var (executor, rendered) = NewExecutor(workflow);
        await executor.EnterAsync();
        rendered.Clear();

        var edge = executor.Navigator.CurrentStage!.FindEdgeByLabel("balance")!;
        var outcome = await executor.AdvanceAlongAsync(edge);

        var fb = Assert.IsType<AdvanceOutcome.AdvancedToFallback>(outcome);
        Assert.Equal("verify", fb.NewStage.Id);
        Assert.Equal(["verify"], rendered);
    }

    [Fact]
    public async Task AdvanceAlong_AmbiguousTarget_UsesChosenEdgePredicate()
    {
        // Two edges from 'start' to the SAME target stage 'servicing' with different
        // labels + predicates. Resolving by stage id would collapse both to the first
        // edge; AdvanceAlongAsync must honor the exact edge the caller chose.
        var workflow = new WorkflowGraphCompiler().Compile(new WorkflowBlueprint
        {
            Id = "ambiguous",
            InitialStageId = "start",
            Stages =
            [
                new StageBlueprint
                {
                    Id = "start",
                    Transitions =
                    [
                        // First edge blocks (state has no 'vip_flag').
                        new TransitionBlueprint
                        {
                            TargetStageId = "servicing",
                            Label = "vip",
                            Requires = [PredicateRef.StateHas("vip_flag")],
                        },
                        // Second edge always passes.
                        new TransitionBlueprint { TargetStageId = "servicing", Label = "standard" },
                    ],
                },
                new StageBlueprint { Id = "servicing", Terminal = true },
            ],
        });

        var (executor, _) = NewExecutor(workflow);
        await executor.EnterAsync();

        var vipEdge = executor.Navigator.CurrentStage!.FindEdgeByLabel("vip")!;
        var blocked = await executor.AdvanceAlongAsync(vipEdge);
        Assert.IsType<AdvanceOutcome.Denied>(blocked);
        Assert.Equal("start", executor.Navigator.CurrentStage!.Id);

        var standardEdge = executor.Navigator.CurrentStage!.FindEdgeByLabel("standard")!;
        var allowed = await executor.AdvanceAlongAsync(standardEdge);
        var advanced = Assert.IsType<AdvanceOutcome.Advanced>(allowed);
        Assert.Equal("servicing", advanced.NewStage.Id);
    }
}
