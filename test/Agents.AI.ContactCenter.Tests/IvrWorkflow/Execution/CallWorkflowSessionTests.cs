using global::Agents.AI.ContactCenter.IvrWorkflow;
using global::Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using global::Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using global::Agents.AI.ContactCenter.IvrWorkflow.Execution;
using global::Agents.AI.ContactCenter.IvrWorkflow.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Execution;

public sealed class CallWorkflowSessionTests
{
    private static AIFunction StubFunction(string name) =>
        AIFunctionFactory.Create(() => $"hi from {name}", name);

    private static CompiledStage StageWithBindings(params ToolBinding[] bindings) =>
        new(
            new StageBlueprint { Id = "only", Goal = "test" },
            outgoingEdges: [],
            toolBindings: bindings);

    private static CompiledCallWorkflow WorkflowWith(CompiledStage stage) =>
        new(
            new WorkflowBlueprint
            {
                Id = "wf",
                InitialStageId = "only",
                Stages = [stage.Blueprint],
            },
            stages: [stage]);

    private static CallWorkflowSession NewSession(CompiledCallWorkflow wf, IServiceProvider? sp = null)
    {
        sp ??= new ServiceCollection().BuildServiceProvider();
        return new CallWorkflowSession(wf, new IvrWorkflowState(), sp);
    }

    [Fact]
    public void GetToolsFor_MaterializesEveryBinding_PreservingOrder()
    {
        var stage = StageWithBindings(
            new ToolBinding("alpha", ServiceLifetime.Singleton, _ => StubFunction("alpha")),
            new ToolBinding("beta", ServiceLifetime.Scoped, _ => StubFunction("beta")),
            new ToolBinding("gamma", ServiceLifetime.Transient, _ => StubFunction("gamma")));
        var session = NewSession(WorkflowWith(stage));

        var tools = session.GetToolsFor(stage);

        Assert.Equal(3, tools.Count);
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, tools.OfType<AIFunction>().Select(f => f.Name));
    }

    [Fact]
    public void GetToolsFor_CachesByStageIdentity_ReturnsSameReference()
    {
        var stage = StageWithBindings(
            new ToolBinding("alpha", ServiceLifetime.Singleton, _ => StubFunction("alpha")));
        var session = NewSession(WorkflowWith(stage));

        var first = session.GetToolsFor(stage);
        var second = session.GetToolsFor(stage);

        Assert.Same(first, second);
    }

    [Fact]
    public void GetToolsFor_InvokesFactoryOncePerStage()
    {
        var invocations = 0;
        var stage = StageWithBindings(
            new ToolBinding(
                "counted",
                ServiceLifetime.Scoped,
                _ =>
                {
                    invocations++;
                    return StubFunction("counted");
                }));
        var session = NewSession(WorkflowWith(stage));

        _ = session.GetToolsFor(stage);
        _ = session.GetToolsFor(stage);
        _ = session.GetToolsFor(stage);

        Assert.Equal(1, invocations);
    }

    [Fact]
    public void GetToolsFor_DifferentStageInstances_HaveSeparateCacheEntries()
    {
        var stageA = new CompiledStage(
            new StageBlueprint { Id = "a", Goal = "test" },
            outgoingEdges: [],
            toolBindings: [new ToolBinding("alpha", ServiceLifetime.Singleton, _ => StubFunction("alpha"))]);
        var stageB = new CompiledStage(
            new StageBlueprint { Id = "b", Goal = "test" },
            outgoingEdges: [],
            toolBindings: [new ToolBinding("beta", ServiceLifetime.Singleton, _ => StubFunction("beta"))]);

        var workflow = new CompiledCallWorkflow(
            new WorkflowBlueprint
            {
                Id = "wf",
                InitialStageId = "a",
                Stages = [stageA.Blueprint, stageB.Blueprint],
            },
            stages: [stageA, stageB]);

        var session = NewSession(workflow);

        var toolsA = session.GetToolsFor(stageA);
        var toolsB = session.GetToolsFor(stageB);

        Assert.NotSame(toolsA, toolsB);
        Assert.Equal("alpha", Assert.IsAssignableFrom<AIFunction>(toolsA[0]).Name);
        Assert.Equal("beta", Assert.IsAssignableFrom<AIFunction>(toolsB[0]).Name);
    }

    [Fact]
    public void GetToolsFor_EmptyToolBindings_ReturnsCachedEmptyList()
    {
        var stage = StageWithBindings(); // no bindings.
        var session = NewSession(WorkflowWith(stage));

        var first = session.GetToolsFor(stage);
        var second = session.GetToolsFor(stage);

        Assert.Empty(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void GetToolsFor_FactoryReceivesSessionServicesProvider()
    {
        IServiceProvider? captured = null;
        var stage = StageWithBindings(
            new ToolBinding(
                "alpha",
                ServiceLifetime.Scoped,
                sp =>
                {
                    captured = sp;
                    return StubFunction("alpha");
                }));

        var services = new ServiceCollection().BuildServiceProvider();
        var session = new CallWorkflowSession(WorkflowWith(stage), new IvrWorkflowState(), services);

        _ = session.GetToolsFor(stage);

        Assert.Same(services, captured);
    }

    [Fact]
    public void GetToolsFor_ThrowsOnNullStage()
    {
        var session = NewSession(WorkflowWith(StageWithBindings()));

        Assert.Throws<ArgumentNullException>(() => session.GetToolsFor(null!));
    }
}
