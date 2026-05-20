using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.DependencyInjection;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;
using Agents.AI.ContactCenter.IvrWorkflow.Workflows;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Workflows;

/// <summary>
/// Coverage for the live-mode <see cref="IvrStageExecutor"/> + <see cref="IIvrStageRunner"/>
/// contract introduced as the foundation for promoting the bridged
/// <see cref="Microsoft.Agents.AI.Workflows.Workflow"/> from preview projection to live
/// orchestrator.
/// </summary>
public class IvrStageRunnerTests
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
        using var sp = BuildFramework();
        var loader = sp.GetRequiredService<IIvrWorkflowLoader>();
        return loader.LoadAsync(System.IO.Path.GetFileNameWithoutExtension(fileName)).AsTask().Result;
    }

    [Fact]
    public void Transition_factory_requires_non_empty_next_stage()
    {
        Assert.Throws<System.ArgumentException>(() => IvrStageOutcome.Transition(string.Empty));
        Assert.Throws<System.ArgumentException>(() => IvrStageOutcome.Transition("   "));

        var outcome = IvrStageOutcome.Transition("verify-account");
        Assert.Equal(IvrStageOutcomeKind.Transition, outcome.Kind);
        Assert.Equal("verify-account", outcome.NextStageId);
    }

    [Fact]
    public void Outcome_factories_set_kind_correctly()
    {
        Assert.Equal(IvrStageOutcomeKind.Complete,
            IvrStageOutcome.Complete(IvrWorkflowStatus.Completed).Kind);
        Assert.Equal(IvrStageOutcomeKind.Retry,
            IvrStageOutcome.Retry("tier swap").Kind);
        Assert.Equal(IvrStageOutcomeKind.Faulted,
            IvrStageOutcome.Faulted("boom", new System.InvalidOperationException()).Kind);
    }

    [Fact]
    public async Task IvrStageExecutor_forwards_NextStageIdHint_from_runner_outcome()
    {
        var compiled = CompileSample("banking-main.yaml");

        var trace = new ConcurrentBag<string>();
        var runner = new ScriptedStageRunner(stage => stage.Id switch
        {
            "greeting" => IvrStageOutcome.Transition("verify-account"),
            "verify-account" => IvrStageOutcome.Transition("route"),
            "route" => IvrStageOutcome.Transition("fulfill-balance"),
            "fulfill-balance" => IvrStageOutcome.Transition("wrap-up"),
            // wrap-up is terminal; the executor will yield on terminal stages.
            _ => IvrStageOutcome.Complete(),
        }, trace);

        var workflow = new IvrWorkflowGraphBuilder().Build(compiled, _ => runner);

        var run = await InProcessExecution.RunAsync(
            workflow,
            new IvrStageMessage(compiled.Stages[0].Id));

        // Workflow should have walked the scripted path and yielded an output at the terminal stage.
        Assert.Contains("greeting", trace);
        Assert.Contains("verify-account", trace);
        Assert.Contains("route", trace);
        Assert.Contains("fulfill-balance", trace);
        Assert.Contains("wrap-up", trace);

        var outputs = run.NewEvents.OfType<WorkflowOutputEvent>().ToList();
        Assert.NotEmpty(outputs);
    }

    [Fact]
    public async Task IvrStageExecutor_yields_output_on_complete_outcome()
    {
        var compiled = CompileSample("banking-main.yaml");

        var trace = new ConcurrentBag<string>();
        var runner = new ScriptedStageRunner(_ => IvrStageOutcome.Complete(IvrWorkflowStatus.Completed), trace);

        var workflow = new IvrWorkflowGraphBuilder().Build(compiled, _ => runner);
        var run = await InProcessExecution.RunAsync(workflow, new IvrStageMessage(compiled.Stages[0].Id));

        // First (and only) stage runs and completes; no transition fires.
        Assert.Equal(new[] { compiled.Stages[0].Id }, trace.OrderBy(s => s).Distinct().ToArray());
        var outputs = run.NewEvents.OfType<WorkflowOutputEvent>().ToList();
        Assert.NotEmpty(outputs);
        Assert.IsType<IvrStageMessage>(outputs[0].As<object>());
    }

    [Fact]
    public async Task IvrStageExecutor_emits_faulted_output_when_runner_returns_Faulted()
    {
        var compiled = CompileSample("banking-main.yaml");

        var runner = new ScriptedStageRunner(_ => IvrStageOutcome.Faulted("nlu down", new System.InvalidOperationException("backend")), trace: null);
        var workflow = new IvrWorkflowGraphBuilder().Build(compiled, _ => runner);

        var run = await InProcessExecution.RunAsync(workflow, new IvrStageMessage(compiled.Stages[0].Id));

        var outputs = run.NewEvents.OfType<WorkflowOutputEvent>().ToList();
        Assert.NotEmpty(outputs);
        var fault = outputs.Select(o => o.As<object>()).OfType<IvrStageFaultedOutput>().FirstOrDefault();
        Assert.NotNull(fault);
        Assert.Equal(compiled.Stages[0].Id, fault!.StageId);
        Assert.Equal("nlu down", fault.Reason);
        Assert.IsType<System.InvalidOperationException>(fault.Exception);
    }

    [Fact]
    public async Task IvrStageExecutor_emits_faulted_output_when_runner_throws()
    {
        var compiled = CompileSample("banking-main.yaml");

        var runner = new ThrowingStageRunner(new System.InvalidOperationException("boom"));
        var workflow = new IvrWorkflowGraphBuilder().Build(compiled, _ => runner);

        var run = await InProcessExecution.RunAsync(workflow, new IvrStageMessage(compiled.Stages[0].Id));

        var outputs = run.NewEvents.OfType<WorkflowOutputEvent>().ToList();
        var fault = outputs.Select(o => o.As<object>()).OfType<IvrStageFaultedOutput>().FirstOrDefault();
        Assert.NotNull(fault);
        Assert.Equal(compiled.Stages[0].Id, fault!.StageId);
        Assert.Equal("boom", fault.Reason);
    }

    [Fact]
    public void Preview_mode_executor_is_default_when_no_runner_selector_is_used()
    {
        var compiled = CompileSample("banking-main.yaml");
        var workflow = new IvrWorkflowGraphBuilder().Build(compiled);

        var executors = workflow.ReflectExecutors();
        Assert.All(executors.Values, binding =>
        {
            var executor = Assert.IsType<IvrStageExecutor>(binding.RawValue);
            Assert.Null(executor.Runner);
        });
    }

    [Fact]
    public void Live_mode_binds_runner_to_each_executor()
    {
        var compiled = CompileSample("banking-main.yaml");
        var runner = new ScriptedStageRunner(_ => IvrStageOutcome.Complete(), trace: null);

        var workflow = new IvrWorkflowGraphBuilder().Build(compiled, _ => runner);

        var executors = workflow.ReflectExecutors();
        Assert.All(executors.Values, binding =>
        {
            var executor = Assert.IsType<IvrStageExecutor>(binding.RawValue);
            Assert.Same(runner, executor.Runner);
        });
    }

    [Fact]
    public void Runner_selector_returning_null_for_a_stage_falls_back_to_preview_mode_for_that_stage()
    {
        var compiled = CompileSample("banking-main.yaml");
        var runner = new ScriptedStageRunner(_ => IvrStageOutcome.Complete(), trace: null);

        var workflow = new IvrWorkflowGraphBuilder().Build(
            compiled,
            stage => stage.Id == "greeting" ? runner : null);

        var executors = workflow.ReflectExecutors();
        var greeting = Assert.IsType<IvrStageExecutor>(executors["greeting"].RawValue);
        var verify = Assert.IsType<IvrStageExecutor>(executors["verify-account"].RawValue);

        Assert.Same(runner, greeting.Runner);
        Assert.Null(verify.Runner);
    }

    private sealed class ScriptedStageRunner : IIvrStageRunner
    {
        private readonly System.Func<CompiledIvrStage, IvrStageOutcome> _script;
        private readonly ConcurrentBag<string>? _trace;

        public ScriptedStageRunner(
            System.Func<CompiledIvrStage, IvrStageOutcome> script,
            ConcurrentBag<string>? trace)
        {
            _script = script;
            _trace = trace;
        }

        public ValueTask<IvrStageOutcome> EnterStageAsync(
            CompiledIvrStage stage,
            IvrStageMessage incoming,
            CancellationToken cancellationToken)
        {
            _trace?.Add(stage.Id);
            return ValueTask.FromResult(_script(stage));
        }
    }

    private sealed class ThrowingStageRunner : IIvrStageRunner
    {
        private readonly System.Exception _exception;
        public ThrowingStageRunner(System.Exception exception) { _exception = exception; }

        public ValueTask<IvrStageOutcome> EnterStageAsync(
            CompiledIvrStage stage,
            IvrStageMessage incoming,
            CancellationToken cancellationToken) =>
            throw _exception;
    }
}
