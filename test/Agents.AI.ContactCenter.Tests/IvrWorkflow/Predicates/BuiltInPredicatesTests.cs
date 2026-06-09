using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Predicates;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Predicates;

public sealed class BuiltInPredicatesTests
{
    private static WorkflowEdgeContext NewContext(
        CallerAuthenticationState? auth = null,
        IvrWorkflowState? state = null)
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        return new WorkflowEdgeContext(state ?? new IvrWorkflowState(), auth, sp);
    }

    [Fact]
    public async Task Always_AllowsAll()
    {
        var result = await BuiltInPredicates.Always()(NewContext(), default);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task Never_AlwaysDeniesWithReason()
    {
        var result = await BuiltInPredicates.Never("blocked")(NewContext(), default);
        Assert.False(result.Passed);
        Assert.Equal("blocked", result.FailureReason);
    }

    [Fact]
    public async Task AuthVerificationLevel_DeniesWhenNoState()
    {
        var result = await BuiltInPredicates.AuthVerificationLevel(CallerVerificationLevel.KnowledgeBased)(NewContext(auth: null), default);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task AuthVerificationLevel_DeniesWhenLevelIsLower()
    {
        var auth = new CallerAuthenticationState();
        // Caller is at AniMatch (10), KBA requires 20.
        auth.TryPromote(CallerIdentity.Anonymous with
        {
            UserId = "u",
            VerificationLevel = CallerVerificationLevel.AniMatch,
        });

        var result = await BuiltInPredicates.AuthVerificationLevel(CallerVerificationLevel.KnowledgeBased)(NewContext(auth), default);

        Assert.False(result.Passed);
        Assert.Contains("AniMatch", result.FailureReason);
        Assert.Contains("KnowledgeBased", result.FailureReason);
    }

    [Fact]
    public async Task AuthVerificationLevel_AllowsWhenLevelMet()
    {
        var auth = new CallerAuthenticationState();
        auth.TryPromote(CallerIdentity.Anonymous with
        {
            UserId = "u",
            VerificationLevel = CallerVerificationLevel.MultiFactor,
        });

        var result = await BuiltInPredicates.AuthVerificationLevel(CallerVerificationLevel.KnowledgeBased)(NewContext(auth), default);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task StateHas_AllowsWhenKeyPresent()
    {
        var state = new IvrWorkflowState();
        state.Set("verified", true);

        var result = await BuiltInPredicates.StateHas("verified")(NewContext(state: state), default);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task StateHas_DeniesWhenKeyMissing()
    {
        var result = await BuiltInPredicates.StateHas("verified")(NewContext(state: new IvrWorkflowState()), default);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task StateEquals_AllowsExactMatch()
    {
        var state = new IvrWorkflowState();
        state.Set("intent", "balance");

        var result = await BuiltInPredicates.StateEquals("intent", "balance")(NewContext(state: state), default);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task StateEquals_DeniesMismatch()
    {
        var state = new IvrWorkflowState();
        state.Set("intent", "transfer");

        var result = await BuiltInPredicates.StateEquals("intent", "balance")(NewContext(state: state), default);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task All_ShortCircuitsOnFirstDeny()
    {
        var firstCalled = false;
        var secondCalled = false;
        EdgePredicate first = (_, _) => { firstCalled = true; return ValueTask.FromResult(EdgePredicateResult.Deny("nope")); };
        EdgePredicate second = (_, _) => { secondCalled = true; return ValueTask.FromResult(EdgePredicateResult.Allow()); };

        var result = await BuiltInPredicates.All(first, second)(NewContext(), default);

        Assert.False(result.Passed);
        Assert.Equal("nope", result.FailureReason);
        Assert.True(firstCalled);
        Assert.False(secondCalled);
    }

    [Fact]
    public async Task All_AllowsWhenAllPass()
    {
        EdgePredicate a = (_, _) => ValueTask.FromResult(EdgePredicateResult.Allow());
        EdgePredicate b = (_, _) => ValueTask.FromResult(EdgePredicateResult.Allow());

        var result = await BuiltInPredicates.All(a, b)(NewContext(), default);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task Any_ShortCircuitsOnFirstAllow()
    {
        var secondCalled = false;
        EdgePredicate first = (_, _) => ValueTask.FromResult(EdgePredicateResult.Allow());
        EdgePredicate second = (_, _) => { secondCalled = true; return ValueTask.FromResult(EdgePredicateResult.Deny("x")); };

        var result = await BuiltInPredicates.Any(first, second)(NewContext(), default);

        Assert.True(result.Passed);
        Assert.False(secondCalled);
    }

    [Fact]
    public async Task Any_ReturnsLastDenialWhenAllDeny()
    {
        EdgePredicate a = (_, _) => ValueTask.FromResult(EdgePredicateResult.Deny("first"));
        EdgePredicate b = (_, _) => ValueTask.FromResult(EdgePredicateResult.Deny("second"));

        var result = await BuiltInPredicates.Any(a, b)(NewContext(), default);

        Assert.False(result.Passed);
        Assert.Equal("second", result.FailureReason);
    }

    [Fact]
    public async Task Not_InvertsAllow()
    {
        var inner = BuiltInPredicates.Always();
        var result = await BuiltInPredicates.Not(inner, "matched")(NewContext(), default);

        Assert.False(result.Passed);
        Assert.Equal("matched", result.FailureReason);
    }

    [Fact]
    public async Task Not_InvertsDeny()
    {
        var inner = BuiltInPredicates.Never("x");
        var result = await BuiltInPredicates.Not(inner)(NewContext(), default);

        Assert.True(result.Passed);
    }
}
