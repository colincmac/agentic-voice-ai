using global::Agents.AI.ContactCenter.IvrWorkflow;
using global::Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using global::Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using global::Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Execution;

public sealed class CallWorkflowSessionFactoryTests
{
    private static CompiledCallWorkflow Trivial() => new WorkflowGraphCompiler().Compile(new WorkflowBlueprint
    {
        Id = "trivial",
        InitialStageId = "start",
        Stages = [new StageBlueprint { Id = "start", Terminal = true }],
    });

    [Fact]
    public void Create_WithoutRestoreFrom_ProducesNewState()
    {
        var factory = new CallWorkflowSessionFactory();
        var sp = new ServiceCollection().BuildServiceProvider();

        var session = factory.Create(Trivial(), sp);

        Assert.NotNull(session.State);
        Assert.Equal(IvrWorkflowStatus.NotStarted, session.State.Status);
        Assert.Null(session.State.CurrentStepName);
    }

    [Fact]
    public void Create_WithRestoreFrom_ReusesState()
    {
        var factory = new CallWorkflowSessionFactory();
        var sp = new ServiceCollection().BuildServiceProvider();

        var preExisting = new IvrWorkflowState { CurrentStepName = "start" };
        preExisting.Set("intent", "balance");

        var session = factory.Create(Trivial(), sp, preExisting);

        Assert.Same(preExisting, session.State);
        Assert.Equal("balance", session.State.Get<string>("intent"));
    }
}
