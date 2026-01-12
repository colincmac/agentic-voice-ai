using Agents.AI.Extensions.LiveVoice.IvrWorkflow;

namespace Agents.AI.Extensions.Tests.IvrWorkflow;

public class IvrWorkflowGuardsTests
{
    [Fact]
    public async Task RequiredStateGuard_PassesWhenStateExists()
    {
        var state = new IvrWorkflowState();
        state.Set("requiredKey", "value");
        var guard = new RequiredStateGuard("requiredKey");

        var result = await guard.EvaluateAsync(state);

        Assert.True(result.Passed);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task RequiredStateGuard_FailsWhenStateMissing()
    {
        var state = new IvrWorkflowState();
        var guard = new RequiredStateGuard("missingKey", "Custom message");

        var result = await guard.EvaluateAsync(state);

        Assert.False(result.Passed);
        Assert.Equal("Custom message", result.FailureReason);
    }

    [Fact]
    public async Task PreviousStepCompletedGuard_PassesWhenStepCompleted()
    {
        var state = new IvrWorkflowState();
        state.MarkStepCompleted("step1");
        var guard = new PreviousStepCompletedGuard("step1");

        var result = await guard.EvaluateAsync(state);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task PreviousStepCompletedGuard_FailsWhenStepNotCompleted()
    {
        var state = new IvrWorkflowState();
        var guard = new PreviousStepCompletedGuard("step1");

        var result = await guard.EvaluateAsync(state);

        Assert.False(result.Passed);
        Assert.Contains("step1", result.FailureReason);
    }

    [Fact]
    public async Task PredicateGuard_PassesWhenPredicateReturnsTrue()
    {
        var state = new IvrWorkflowState();
        state.Set("count", 5);
        var guard = new PredicateGuard(s => s.Get<int>("count") > 3, "Count must be > 3");

        var result = await guard.EvaluateAsync(state);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task PredicateGuard_FailsWhenPredicateReturnsFalse()
    {
        var state = new IvrWorkflowState();
        state.Set("count", 2);
        var guard = new PredicateGuard(s => s.Get<int>("count") > 3, "Count must be > 3");

        var result = await guard.EvaluateAsync(state);

        Assert.False(result.Passed);
        Assert.Equal("Count must be > 3", result.FailureReason);
    }

    [Fact]
    public async Task AsyncPredicateGuard_PassesWhenPredicateReturnsTrue()
    {
        var state = new IvrWorkflowState();
        state.Set("valid", true);
        var guard = new AsyncPredicateGuard(
            async (s, ct) =>
            {
                await Task.Delay(1, ct);
                return s.Get<bool>("valid");
            },
            "Not valid");

        var result = await guard.EvaluateAsync(state);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task NonEmptyStringValidator_PassesForNonEmptyString()
    {
        var state = new IvrWorkflowState();
        state.Set("name", "John");
        var validator = new NonEmptyStringValidator("name");

        var result = await validator.ValidateAsync(state);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task NonEmptyStringValidator_FailsForEmptyString()
    {
        var state = new IvrWorkflowState();
        state.Set("name", "");
        var validator = new NonEmptyStringValidator("name", "Name is required");

        var result = await validator.ValidateAsync(state);

        Assert.False(result.Passed);
        Assert.Equal("Name is required", result.FailureReason);
    }

    [Fact]
    public async Task NonEmptyStringValidator_FailsForWhitespace()
    {
        var state = new IvrWorkflowState();
        state.Set("name", "   ");
        var validator = new NonEmptyStringValidator("name");

        var result = await validator.ValidateAsync(state);

        Assert.False(result.Passed);
    }

    [Fact]
    public async Task PatternValidator_PassesForMatchingPattern()
    {
        var state = new IvrWorkflowState();
        state.Set("digits", "1234");
        var validator = new PatternValidator("digits", @"^\d{4}$", "Must be 4 digits");

        var result = await validator.ValidateAsync(state);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task PatternValidator_FailsForNonMatchingPattern()
    {
        var state = new IvrWorkflowState();
        state.Set("digits", "123");
        var validator = new PatternValidator("digits", @"^\d{4}$", "Must be 4 digits");

        var result = await validator.ValidateAsync(state);

        Assert.False(result.Passed);
        Assert.Equal("Must be 4 digits", result.FailureReason);
    }

    [Fact]
    public async Task PatternValidator_FailsForEmptyValue()
    {
        var state = new IvrWorkflowState();
        var validator = new PatternValidator("digits", @"^\d{4}$", "Must be 4 digits");

        var result = await validator.ValidateAsync(state);

        Assert.False(result.Passed);
    }

    [Fact]
    public async Task PredicateValidator_PassesWhenPredicateReturnsTrue()
    {
        var state = new IvrWorkflowState();
        state.Set("amount", 100);
        var validator = new PredicateValidator(s => s.Get<int>("amount") >= 50, "Amount must be >= 50");

        var result = await validator.ValidateAsync(state);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task AsyncValidator_WorksWithAsyncValidation()
    {
        var state = new IvrWorkflowState();
        state.Set("id", "valid-123");
        var validator = new AsyncValidator(async (s, ct) =>
        {
            await Task.Delay(1, ct);
            var id = s.Get<string>("id");
            return id?.StartsWith("valid") == true
                ? IvrGuardResult.Pass()
                : IvrGuardResult.Fail("Invalid ID");
        });

        var result = await validator.ValidateAsync(state);

        Assert.True(result.Passed);
    }
}
