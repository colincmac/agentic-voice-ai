using System.Linq;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.DependencyInjection;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;
using Agents.AI.ContactCenter.IvrWorkflow.Workflows;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Workflows;

public class IvrWorkflowGraphBuilderTests
{
    private static readonly string[] sampleToolNames =
    [
        "balance-lookup",
        "activate-card",
        "verify-account-number",
        "send-otp",
        "verify-otp",
    ];

    private static ServiceProvider BuildFramework()
    {
        // Register the framework with stub tools the YAML samples reference. Built-in tools
        // (transfer-to-human, end-session) are added automatically by the tool registry.
        var services = new ServiceCollection();
        services.AddIvrWorkflowFramework(b =>
        {
            b.AddFileSystemSource(
                System.IO.Path.Combine(System.AppContext.BaseDirectory, "IvrWorkflow", "Samples"));

            foreach (var name in sampleToolNames)
            {
                b.AddTool(name, AIFunctionFactory.Create(
                    () => "stub",
                    new AIFunctionFactoryOptions { Name = name }));
            }
        });
        return services.BuildServiceProvider();
    }

    private static CompiledIvrWorkflow CompileSample(string fileName)
    {
        var path = System.IO.Path.Combine(
            System.AppContext.BaseDirectory,
            "IvrWorkflow",
            "Samples",
            fileName);
        Assert.True(System.IO.File.Exists(path), $"Sample YAML missing at {path}");

        using var sp = BuildFramework();
        var loader = sp.GetRequiredService<IIvrWorkflowLoader>();
        return loader.LoadAsync(System.IO.Path.GetFileNameWithoutExtension(fileName)).AsTask().Result;
    }

    [Fact]
    public void Build_BankingMain_ProducesNodePerStageAndKnownEdges()
    {
        var compiled = CompileSample("banking-main.yaml");
        var builder = new IvrWorkflowGraphBuilder();

        var workflow = builder.Build(compiled);

        Assert.Equal("banking-main", workflow.Name);
        Assert.Equal(compiled.Stages[0].Id, workflow.StartExecutorId);

        var executors = workflow.ReflectExecutors();
        foreach (var stage in compiled.Stages)
        {
            Assert.True(executors.ContainsKey(stage.Id), $"Missing executor for stage '{stage.Id}'");
        }

        var edges = workflow.ReflectEdges();
        // greeting -> verify-account, greeting -> verify-identity
        Assert.True(edges.ContainsKey("greeting"));
        // verify-account -> route
        Assert.True(edges.ContainsKey("verify-account"));
        // route -> fulfill-balance, route -> fulfill-card-activation
        Assert.True(edges.ContainsKey("route"));
        // fulfill-balance -> wrap-up
        Assert.True(edges.ContainsKey("fulfill-balance"));

        // wrap-up is terminal: no outgoing transitions in the YAML, so it should not appear
        // as an edge source.
        Assert.False(edges.ContainsKey("wrap-up"));
    }

    [Fact]
    public void Build_UtilityBillPay_ProducesLinearChainAndTerminalStage()
    {
        var compiled = CompileSample("utility-bill-pay.yaml");
        var builder = new IvrWorkflowGraphBuilder();

        var workflow = builder.Build(compiled);

        Assert.Equal("utility-bill-pay", workflow.Name);
        Assert.Equal("menu", workflow.StartExecutorId);

        var executors = workflow.ReflectExecutors();
        Assert.Equal(4, executors.Count); // menu, collect-account, confirm, complete

        var edges = workflow.ReflectEdges();
        Assert.True(edges.ContainsKey("menu"));
        Assert.True(edges.ContainsKey("collect-account"));
        Assert.True(edges.ContainsKey("confirm"));
        Assert.False(edges.ContainsKey("complete")); // terminal stage, no outgoing edges

        // Both menu options point to collect-account; the builder dedupes by target so we
        // expect exactly one outgoing edge from "menu".
        Assert.Single(edges["menu"]);

        // confirm has two distinct targets: complete and menu (restart).
        Assert.Equal(2, edges["confirm"].Count);
    }

    [Fact]
    public void Build_MissingTransitionTarget_ThrowsTypedException()
    {
        var compiled = CompileSample("utility-bill-pay.yaml");

        // Mutate the compiled stages so the first stage points at a non-existent target.
        var poisoned = new CompiledIvrWorkflow
        {
            Name = compiled.Name,
            Description = compiled.Description,
            Version = compiled.Version,
            Runtime = compiled.Runtime,
            Strategy = compiled.Strategy,
            Stages = [.. compiled.Stages.Select((s, i) => i == 0 ? CloneWithBadTransition(s) : s)],
            Capabilities = compiled.Capabilities,
            IntentExamples = compiled.IntentExamples,
            Source = compiled.Source,
        };

        var builder = new IvrWorkflowGraphBuilder();
        var ex = Assert.Throws<IvrWorkflowGraphBuildException>(() => builder.Build(poisoned));
        Assert.Equal(poisoned.Name, ex.Workflow);
    }

    [Fact]
    public async Task BuildGraphAsync_LoaderExtension_ProducesSameGraph()
    {
        using var sp = BuildFramework();

        var loader = sp.GetRequiredService<IIvrWorkflowLoader>();
        var graphBuilder = sp.GetRequiredService<IIvrWorkflowGraphBuilder>();

        var workflow = await loader.BuildGraphAsync(graphBuilder, "utility-bill-pay");

        Assert.Equal("utility-bill-pay", workflow.Name);
        Assert.Equal("menu", workflow.StartExecutorId);
    }

    private static CompiledIvrStage CloneWithBadTransition(CompiledIvrStage stage)
    {
        // Re-use the runtime step but override transitions with a target that doesn't exist.
        var runtime = new RealtimeIvrWorkflowStep
        {
            Id = stage.Id,
            ConversationState = stage.RuntimeStep.ConversationState with
            {
                Transitions =
                [
                    new Agents.AI.Extensions.RealtimeAgentHelpers.Prompting.StateTransition
                    {
                        NextStep = "nonexistent-target",
                        Condition = "always",
                    },
                ],
            },
            AvailableTools = stage.RuntimeStep.AvailableTools,
            ToolRules = stage.RuntimeStep.ToolRules,
            Guards = stage.RuntimeStep.Guards,
            Validators = stage.RuntimeStep.Validators,
            RequiredStateKeys = stage.RuntimeStep.RequiredStateKeys,
            MaxRetries = stage.RuntimeStep.MaxRetries,
            MaxDuration = stage.RuntimeStep.MaxDuration,
            RequiredAuthLevel = stage.RuntimeStep.RequiredAuthLevel,
            OnCompleted = stage.RuntimeStep.OnCompleted,
            StepDtmfConfiguration = stage.RuntimeStep.StepDtmfConfiguration,
        };

        return new CompiledIvrStage
        {
            Id = stage.Id,
            Description = stage.Description,
            Goal = stage.Goal,
            Terminal = stage.Terminal,
            Strategy = stage.Strategy,
            Tools = stage.Tools,
            Capabilities = stage.Capabilities,
            Intents = stage.Intents,
            RuntimeStep = runtime,
        };
    }
}
