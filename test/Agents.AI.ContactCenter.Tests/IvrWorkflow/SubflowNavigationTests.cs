using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Strategies;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow;

/// <summary>
/// Phase 1 contract tests for sub-workflow push/pop on <see cref="IvrWorkflowNavigator"/>.
/// Exercises the catalog dependency, frame stack growth, state preservation across the
/// boundary, success/failure routing on pop, and cycle detection.
/// </summary>
public class SubflowNavigationTests
{
    [Fact]
    public async Task PushSubflow_GrowsFrameStack_AndEntersChildInitialStep()
    {
        var (navigator, state) = BuildNavigator();

        navigator.EnterInitialStep();
        Assert.Equal(1, state.FrameDepth);
        Assert.Equal("root", state.CurrentFrame!.WorkflowId);

        var initial = await navigator.PushSubflowAsync("child", returnToStepId: "after", failureReturnStepId: "fallback");

        Assert.Equal(2, state.FrameDepth);
        Assert.Equal("child", state.CurrentFrame!.WorkflowId);
        Assert.Equal("child_start", initial.Id);
        Assert.Equal("after", state.CurrentFrame.ReturnToStepId);
        Assert.Equal("fallback", state.CurrentFrame.FailureReturnStepId);
    }

    [Fact]
    public async Task PopSubflow_Success_RoutesParentToOnSuccess()
    {
        var (navigator, state) = BuildNavigator();
        navigator.EnterInitialStep();
        await navigator.PushSubflowAsync("child", returnToStepId: "after", failureReturnStepId: "fallback");

        var resumed = await navigator.PopFrameAsync(success: true);

        Assert.Equal(1, state.FrameDepth);
        Assert.NotNull(resumed);
        Assert.Equal("after", resumed!.Id);
        Assert.Equal("after", state.CurrentFrame!.CurrentStepId);
    }

    [Fact]
    public async Task PopSubflow_Failure_RoutesParentToOnFailure()
    {
        var (navigator, state) = BuildNavigator();
        navigator.EnterInitialStep();
        await navigator.PushSubflowAsync("child", returnToStepId: "after", failureReturnStepId: "fallback");

        var resumed = await navigator.PopFrameAsync(success: false);

        Assert.NotNull(resumed);
        Assert.Equal("fallback", resumed!.Id);
        Assert.Equal("fallback", state.CurrentFrame!.CurrentStepId);
    }

    [Fact]
    public async Task StateData_IsSharedAcrossFrames()
    {
        var (navigator, state) = BuildNavigator();
        navigator.EnterInitialStep();
        state.Set("CallerFullName", "Jordan Reyes");

        await navigator.PushSubflowAsync("child", returnToStepId: "after", failureReturnStepId: "fallback");

        // Child sees parent-written keys (shared dictionary).
        Assert.Equal("Jordan Reyes", state.Get<string>("CallerFullName"));

        // Child writes propagate back to parent after pop.
        state.Set("pinVerified", true);
        await navigator.PopFrameAsync(success: true);

        Assert.True(state.Get<bool>("pinVerified"));
        Assert.Equal("Jordan Reyes", state.Get<string>("CallerFullName"));
    }

    [Fact]
    public async Task PushSubflow_DetectsCycles()
    {
        var (navigator, state) = BuildNavigator();
        navigator.EnterInitialStep();
        await navigator.PushSubflowAsync("child", returnToStepId: "after", failureReturnStepId: "fallback");

        // Pushing "child" again with it already on the stack must throw.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => navigator.PushSubflowAsync("child", returnToStepId: "after", failureReturnStepId: "fallback"));

        Assert.Equal(2, state.FrameDepth); // stack unchanged
    }

    [Fact]
    public async Task PushSubflow_UnknownId_Throws()
    {
        var (navigator, _) = BuildNavigator();
        navigator.EnterInitialStep();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => navigator.PushSubflowAsync("does-not-exist", returnToStepId: "after", failureReturnStepId: null));
    }

    [Fact]
    public async Task PopFrame_OnRoot_ReturnsNull_AndCompletesWorkflow()
    {
        var (navigator, state) = BuildNavigator();
        navigator.EnterInitialStep();

        var resumed = await navigator.PopFrameAsync(success: true);

        Assert.Null(resumed);
        Assert.Equal(0, state.FrameDepth);
        Assert.Equal(IvrWorkflowStatus.Completed, state.Status);
    }

    private static (IvrWorkflowNavigator navigator, IvrWorkflowState state) BuildNavigator()
    {
        // Parent workflow with one normal stage 'start' + a return step 'after' + 'fallback'.
        var parent = BuildWorkflow(
            "root",
            ("start", terminal: false, transitions: ["after", "fallback"]),
            ("after", terminal: false, transitions: []),
            ("fallback", terminal: false, transitions: []));

        var child = BuildWorkflow(
            "child",
            ("child_start", terminal: false, transitions: ["child_done"]),
            ("child_done", terminal: true, transitions: []));

        var catalog = new InMemoryCatalog();
        catalog.Register("root", parent);
        catalog.Register("child", child);

        var state = new IvrWorkflowState();
        var navigator = new IvrWorkflowNavigator(
            parent.Runtime,
            state,
            services: new ServiceCollection().BuildServiceProvider(),
            catalog);
        return (navigator, state);
    }

    private static CompiledIvrWorkflow BuildWorkflow(
        string name,
        params (string id, bool terminal, string[] transitions)[] stages)
    {
        var steps = new List<RealtimeIvrWorkflowStep>();
        foreach (var (id, terminal, transitions) in stages)
        {
            var ts = transitions.Length == 0
                ? null
                : (IReadOnlyList<StateTransition>)transitions
                    .Select(t => new StateTransition { Condition = "default", NextStep = t })
                    .ToList();
            steps.Add(new RealtimeIvrWorkflowStep
            {
                Id = id,
                ConversationState = new ConversationState
                {
                    Id = id,
                    Description = id,
                    Instructions = [],
                    Transitions = ts,
                },
                Terminal = terminal,
            });
        }
        var runtime = new RealtimeIvrWorkflowDefinition
        {
            Name = name,
            BasePrompt = new RealtimePrompt(),
            Steps = steps,
        };
        return new CompiledIvrWorkflow
        {
            Name = name,
            Runtime = runtime,
            Strategy = IvrStrategyPolicy.Default,
            Stages = [],
            Capabilities = new Dictionary<string, CompiledIvrCapability>(StringComparer.Ordinal),
            IntentExamples = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
        };
    }

    private sealed class InMemoryCatalog : IIvrWorkflowCatalog
    {
        private readonly Dictionary<string, CompiledIvrWorkflow> _byId = new(StringComparer.OrdinalIgnoreCase);

        public void Register(string id, CompiledIvrWorkflow workflow) => _byId[id] = workflow;

        public IReadOnlyCollection<string> Ids => _byId.Keys.ToArray();

        public IReadOnlyCollection<int> VersionsFor(string workflowId)
            => _byId.TryGetValue(workflowId, out var w) ? [w.Version >= 1 ? w.Version : 1] : [];

        public bool TryGet(string workflowId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CompiledIvrWorkflow? workflow)
            => TryGet(workflowId, null, null, out workflow);

        public bool TryGet(
            string workflowId,
            int? minVersion,
            int? maxVersion,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CompiledIvrWorkflow? workflow)
        {
            if (_byId.TryGetValue(workflowId, out var w))
            {
                var v = w.Version >= 1 ? w.Version : 1;
                if ((minVersion is null || v >= minVersion) && (maxVersion is null || v <= maxVersion))
                {
                    workflow = w;
                    return true;
                }
            }
            workflow = null;
            return false;
        }

        public CompiledIvrWorkflow Get(string workflowId)
            => _byId.TryGetValue(workflowId, out var w)
                ? w
                : throw new KeyNotFoundException(workflowId);

        public CompiledIvrWorkflow Get(string workflowId, int? minVersion, int? maxVersion)
            => TryGet(workflowId, minVersion, maxVersion, out var w)
                ? w
                : throw new KeyNotFoundException(workflowId);

        public ValueTask EnsureLoadedAsync(CancellationToken cancellationToken = default) => default;
    }
}
