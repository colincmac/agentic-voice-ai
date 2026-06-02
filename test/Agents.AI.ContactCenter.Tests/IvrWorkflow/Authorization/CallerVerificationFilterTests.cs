using System.ComponentModel;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.IvrWorkflow.Authorization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Authorization;

public sealed class CallerVerificationFilterTests
{
    /// <summary>Tool that requires multi-factor verification.</summary>
    [Description("Look up account balance.")]
    [RequiresCallerVerification(CallerVerificationLevel.MultiFactor, FailureMessage = "Need MFA.")]
    public string LookupBalance() => "balance:$100";

    [Description("Greet caller.")]
    public string Greet() => "hi";

    [Fact]
    public async Task NoRequirement_PassesThrough()
    {
        var fn = AIFunctionFactory.Create(Greet);
        var args = new AIFunctionArguments();
        var nextCalled = false;

        var result = await CallerVerificationFilter.InvokeAsync(
            agent: null,
            arguments: args,
            function: fn,
            next: (_, _) => { nextCalled = true; return ValueTask.FromResult<object?>("hi"); },
            cancellationToken: default);

        Assert.True(nextCalled);
        Assert.Equal("hi", result);
    }

    [Fact]
    public async Task RequirementMet_Invokes()
    {
        var auth = new CallerAuthenticationState();
        auth.TryPromote(CallerIdentity.Anonymous with
        {
            UserId = "u",
            VerificationLevel = CallerVerificationLevel.MultiFactor,
        });

        var sp = new ServiceCollection()
            .AddSingleton(auth)
            .BuildServiceProvider();

        var fn = AIFunctionFactory.Create(LookupBalance);
        var args = new AIFunctionArguments { Services = sp };

        var result = await CallerVerificationFilter.InvokeAsync(
            agent: null,
            arguments: args,
            function: fn,
            next: (_, _) => ValueTask.FromResult<object?>("ok"),
            cancellationToken: default);

        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task RequirementUnmet_ShortCircuitsWithFailureMessage()
    {
        var auth = new CallerAuthenticationState(); // Anonymous, level None
        var sp = new ServiceCollection()
            .AddSingleton(auth)
            .BuildServiceProvider();

        var fn = AIFunctionFactory.Create(LookupBalance);
        var args = new AIFunctionArguments { Services = sp };

        var nextCalled = false;
        var result = await CallerVerificationFilter.InvokeAsync(
            agent: null,
            arguments: args,
            function: fn,
            next: (_, _) => { nextCalled = true; return ValueTask.FromResult<object?>("ok"); },
            cancellationToken: default);

        Assert.False(nextCalled);
        Assert.Equal("Need MFA.", result);
    }

    [Fact]
    public async Task RequirementUnresolvableState_FailsClosed()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var fn = AIFunctionFactory.Create(LookupBalance);
        var args = new AIFunctionArguments { Services = sp };

        var result = await CallerVerificationFilter.InvokeAsync(
            agent: null,
            arguments: args,
            function: fn,
            next: (_, _) => ValueTask.FromResult<object?>("ok"),
            cancellationToken: default);

        Assert.Equal("Need MFA.", result);
    }
}
