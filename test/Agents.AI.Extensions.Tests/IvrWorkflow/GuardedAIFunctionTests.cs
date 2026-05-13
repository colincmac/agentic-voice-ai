using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.Extensions.Tests.IvrWorkflow;

public class GuardedAIFunctionTests
{
    [Fact]
    public async Task Invoke_WithNoGuards_ForwardsToInner()
    {
        var calls = 0;
        var inner = AIFunctionFactory.Create(() => { calls++; return "ok"; }, "do_thing");
        var state = new IvrWorkflowState();
        var guarded = new GuardedAIFunction(inner, [], () => state);

        var result = await guarded.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);

        Assert.Equal("ok", result?.ToString());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Invoke_AllGuardsPass_ForwardsToInner()
    {
        var calls = 0;
        var inner = AIFunctionFactory.Create(() => { calls++; return "forty-two"; }, "do_thing");
        var state = new IvrWorkflowState();
        state.Set("k", "v");
        var guarded = new GuardedAIFunction(
            inner,
            [new RequiredStateGuard("k")],
            () => state);

        var result = await guarded.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);

        Assert.Equal("forty-two", result?.ToString());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Invoke_GuardFails_ReturnsBlockedReasonAndSkipsInner()
    {
        var calls = 0;
        var inner = AIFunctionFactory.Create(() => { calls++; return "ran"; }, "do_thing");
        var state = new IvrWorkflowState();
        var guarded = new GuardedAIFunction(
            inner,
            [new RequiredStateGuard("missing", "PIN must be verified first")],
            () => state);

        var result = await guarded.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);

        Assert.Equal(0, calls);
        Assert.Equal("Action blocked: PIN must be verified first", result?.ToString());
    }

    [Fact]
    public async Task Invoke_FirstFailingGuardShortCircuits()
    {
        var firstEvaluated = 0;
        var secondEvaluated = 0;
        var thirdEvaluated = 0;

        IIvrStepGuard tally(int n, bool passes) => new PredicateGuard(
            _ =>
            {
                if (n == 1) { firstEvaluated++; }
                else if (n == 2) { secondEvaluated++; }
                else { thirdEvaluated++; }
                return passes;
            },
            $"guard {n} failed");

        var calls = 0;
        var inner = AIFunctionFactory.Create(() => { calls++; return "ok"; }, "do_thing");
        var state = new IvrWorkflowState();
        var guarded = new GuardedAIFunction(
            inner,
            [tally(1, true), tally(2, false), tally(3, true)],
            () => state);

        var result = await guarded.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);

        Assert.Equal(1, firstEvaluated);
        Assert.Equal(1, secondEvaluated);
        Assert.Equal(0, thirdEvaluated);
        Assert.Equal(0, calls);
        Assert.Equal("Action blocked: guard 2 failed", result?.ToString());
    }

    [Fact]
    public async Task Invoke_StateAccessorReadAtInvocationTime()
    {
        var calls = 0;
        var inner = AIFunctionFactory.Create(() => { calls++; return "ok"; }, "do_thing");
        var state = new IvrWorkflowState();
        var guarded = new GuardedAIFunction(
            inner,
            [new RequiredStateGuard("pin")],
            () => state);

        var blocked = await guarded.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);
        Assert.StartsWith("Action blocked", blocked?.ToString());
        Assert.Equal(0, calls);

        state.Set("pin", "1234");

        var allowed = await guarded.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);
        Assert.Equal("ok", allowed?.ToString());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Invoke_GuardSeesArgumentsServices()
    {
        var inner = AIFunctionFactory.Create(() => "ok", "do_thing");
        var state = new IvrWorkflowState();
        state.Set("k", "v");

        var guard = new AsyncPredicateGuard(
            (s, _) => Task.FromResult(s.Has("k")),
            "k missing");

        var guarded = new GuardedAIFunction(inner, [guard], () => state);

        using var sp = new ServiceCollection().BuildServiceProvider();
        var args = new AIFunctionArguments { Services = sp };

        var result = await guarded.InvokeAsync(args, TestContext.Current.CancellationToken);

        Assert.Equal("ok", result?.ToString());
    }

    [Fact]
    public async Task WrapTools_WrapsAIFunctionsAndPassesThroughOthers()
    {
        var fnCalls = 0;
        var fn = AIFunctionFactory.Create(() => { fnCalls++; return "ok"; }, "fn");
        var nonFn = new NonInvocableTool("schema");
        var state = new IvrWorkflowState();

        var wrapped = GuardedAIFunction
            .WrapTools(
                [fn, nonFn],
                [new RequiredStateGuard("missing", "blocked")],
                () => state)
            .ToList();

        Assert.IsType<GuardedAIFunction>(wrapped[0]);
        Assert.Same(nonFn, wrapped[1]);

        var result = await ((AIFunction)wrapped[0]).InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, fnCalls);
        Assert.Equal("Action blocked: blocked", result?.ToString());
    }

    [Fact]
    public void WrapTools_WithEmptyGuards_ReturnsInputUnchanged()
    {
        var fn = AIFunctionFactory.Create(() => "ok", "fn");
        var state = new IvrWorkflowState();

        var input = new AITool[] { fn };
        var wrapped = GuardedAIFunction.WrapTools(input, [], () => state);

        Assert.Same(input, wrapped);
    }

    private sealed class NonInvocableTool(string description) : AITool
    {
        public override string Name => "non_invocable";
        public override string Description { get; } = description;
    }
}
