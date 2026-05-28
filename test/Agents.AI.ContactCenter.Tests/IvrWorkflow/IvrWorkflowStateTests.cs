using Agents.AI.ContactCenter.IvrWorkflow;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow;

public class IvrWorkflowStateTests
{
    [Fact]
    public void State_InitializedWithDefaults()
    {
        var state = new IvrWorkflowState();

        Assert.NotEmpty(state.WorkflowId);
        Assert.Null(state.SessionId);
        Assert.Null(state.CurrentStepName);
        Assert.Equal(-1, state.CurrentStepIndex);
        Assert.Equal(IvrWorkflowStatus.NotStarted, state.Status);
        Assert.Empty(state.CompletedSteps);
        Assert.Empty(state.Keys);
    }

    [Fact]
    public void Set_StoresValue()
    {
        var state = new IvrWorkflowState();

        state.Set("name", "John Doe");

        Assert.Equal("John Doe", state.Get<string>("name"));
        Assert.True(state.Has("name"));
    }

    [Fact]
    public void Get_ReturnsDefaultForMissingKey()
    {
        var state = new IvrWorkflowState();

        var result = state.Get<string>("missing");

        Assert.Null(result);
    }

    [Fact]
    public void TryGet_ReturnsTrueForExistingKey()
    {
        var state = new IvrWorkflowState();
        state.Set("value", 42);

        var success = state.TryGet<int>("value", out var value);

        Assert.True(success);
        Assert.Equal(42, value);
    }

    [Fact]
    public void TryGet_ReturnsFalseForMissingKey()
    {
        var state = new IvrWorkflowState();

        var success = state.TryGet<int>("missing", out var value);

        Assert.False(success);
        Assert.Equal(default, value);
    }

    [Fact]
    public void Has_ReturnsFalseForNullValue()
    {
        var state = new IvrWorkflowState();
        state.Set<string?>("nullKey", null);

        Assert.False(state.Has("nullKey"));
    }

    [Fact]
    public void Remove_RemovesValue()
    {
        var state = new IvrWorkflowState();
        state.Set("toRemove", "value");

        var removed = state.Remove("toRemove");

        Assert.True(removed);
        Assert.False(state.Has("toRemove"));
    }

    [Fact]
    public void Remove_ReturnsFalseForMissingKey()
    {
        var state = new IvrWorkflowState();

        var removed = state.Remove("missing");

        Assert.False(removed);
    }

    [Fact]
    public void MarkStepCompleted_TracksCompletion()
    {
        var state = new IvrWorkflowState();

        state.MarkStepCompleted("step1");
        state.MarkStepCompleted("step2");

        Assert.True(state.IsStepCompleted("step1"));
        Assert.True(state.IsStepCompleted("step2"));
        Assert.False(state.IsStepCompleted("step3"));
        Assert.Equal(2, state.CompletedSteps.Count);
    }

    [Fact]
    public void MarkStepCompleted_DoesNotDuplicateSteps()
    {
        var state = new IvrWorkflowState();

        state.MarkStepCompleted("step1");
        state.MarkStepCompleted("step1");

        Assert.Single(state.CompletedSteps);
    }

    [Fact]
    public void GetTimestamp_ReturnsSetTime()
    {
        var state = new IvrWorkflowState();
        var beforeSet = DateTimeOffset.UtcNow;

        state.Set("timed", "value");

        var timestamp = state.GetTimestamp("timed");
        Assert.NotNull(timestamp);
        Assert.True(timestamp >= beforeSet);
    }

    [Fact]
    public void GetTimestamp_ReturnsNullForMissingKey()
    {
        var state = new IvrWorkflowState();

        var timestamp = state.GetTimestamp("missing");

        Assert.Null(timestamp);
    }

    [Fact]
    public void ToSnapshot_ReturnsCurrentState()
    {
        var state = new IvrWorkflowState();
        state.Set("key1", "value1");
        state.Set("key2", 42);

        var snapshot = state.ToSnapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal("value1", snapshot["key1"]);
        Assert.Equal(42, snapshot["key2"]);
    }

    [Fact]
    public void Keys_ReturnsAllSetKeys()
    {
        var state = new IvrWorkflowState();
        state.Set("a", 1);
        state.Set("b", 2);
        state.Set("c", 3);

        var keys = state.Keys;

        Assert.Equal(3, keys.Count);
        Assert.Contains("a", keys);
        Assert.Contains("b", keys);
        Assert.Contains("c", keys);
    }

    [Fact]
    public async Task LastModifiedAt_UpdatesOnSet()
    {
        var state = new IvrWorkflowState();
        var initialModified = state.LastModifiedAt;

        await Task.Delay(10);
        state.Set("key", "value");

        Assert.True(state.LastModifiedAt > initialModified);
    }
}
