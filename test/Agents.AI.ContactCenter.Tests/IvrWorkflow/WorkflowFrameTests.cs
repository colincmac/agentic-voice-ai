using Agents.AI.ContactCenter.IvrWorkflow;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow;

/// <summary>
/// Phase 0 contract tests for the new <see cref="WorkflowFrame"/> stack on
/// <see cref="IvrWorkflowState"/>. Verifies that the back-compat shims
/// (<c>CurrentStepName</c>, <c>CurrentStepIndex</c>, <c>StepStartedAt</c>) route to
/// the top frame, that lazy anonymous-frame creation works, and that push/pop behave
/// as expected. Cross-frame completion semantics (Phase 0 keeps them global) are
/// asserted explicitly so we'll notice if Phase 1 changes them.
/// </summary>
public class WorkflowFrameTests
{
    [Fact]
    public void NewState_HasNoFrames()
    {
        var state = new IvrWorkflowState();

        Assert.Equal(0, state.FrameDepth);
        Assert.Null(state.CurrentFrame);
        Assert.Empty(state.Frames);
        Assert.Null(state.CurrentStepName);
        Assert.Equal(-1, state.CurrentStepIndex);
        Assert.Null(state.StepStartedAt);
    }

    [Fact]
    public void SettingCurrentStepName_OnEmptyState_LazilyCreatesAnonymousFrame()
    {
        var state = new IvrWorkflowState();

        state.CurrentStepName = "welcome";

        Assert.Equal(1, state.FrameDepth);
        Assert.NotNull(state.CurrentFrame);
        Assert.Equal(string.Empty, state.CurrentFrame!.WorkflowId);
        Assert.Equal("welcome", state.CurrentFrame.CurrentStepId);
        Assert.Equal("welcome", state.CurrentStepName);
    }

    [Fact]
    public void ShimSetters_UpdateTheTopFrameInPlace()
    {
        var state = new IvrWorkflowState();
        var stamp = DateTimeOffset.UtcNow;

        state.CurrentStepName = "verify";
        state.CurrentStepIndex = 2;
        state.StepStartedAt = stamp;

        // All three setters share the single lazy anonymous frame.
        Assert.Equal(1, state.FrameDepth);
        Assert.Equal("verify", state.CurrentFrame!.CurrentStepId);
        Assert.Equal(2, state.CurrentFrame.CurrentStepIndex);
        Assert.Equal(stamp, state.CurrentFrame.StepStartedAt);
    }

    [Fact]
    public void PushFrame_ShadowsParent_AndPopRestoresIt()
    {
        var state = new IvrWorkflowState();
        state.PushFrame(new WorkflowFrame
        {
            WorkflowId = "parent",
            CurrentStepId = "welcome",
            CurrentStepIndex = 0,
        });

        state.PushFrame(new WorkflowFrame
        {
            WorkflowId = "verify",
            CurrentStepId = "ask_pin",
            CurrentStepIndex = 0,
            ReturnToStepId = "balance",
        });

        Assert.Equal(2, state.FrameDepth);
        Assert.Equal("verify", state.CurrentFrame!.WorkflowId);
        Assert.Equal("ask_pin", state.CurrentStepName);

        var popped = state.PopFrame();

        Assert.NotNull(popped);
        Assert.Equal("verify", popped!.WorkflowId);
        Assert.Equal(1, state.FrameDepth);
        Assert.Equal("parent", state.CurrentFrame!.WorkflowId);
        Assert.Equal("welcome", state.CurrentStepName);
    }

    [Fact]
    public void Frames_SnapshotIsTopFirst()
    {
        var state = new IvrWorkflowState();
        state.PushFrame(new WorkflowFrame { WorkflowId = "a", CurrentStepId = "a1" });
        state.PushFrame(new WorkflowFrame { WorkflowId = "b", CurrentStepId = "b1" });
        state.PushFrame(new WorkflowFrame { WorkflowId = "c", CurrentStepId = "c1" });

        var snapshot = state.Frames;

        Assert.Equal(3, snapshot.Count);
        Assert.Equal("c", snapshot[0].WorkflowId);
        Assert.Equal("b", snapshot[1].WorkflowId);
        Assert.Equal("a", snapshot[2].WorkflowId);
    }

    [Fact]
    public void PopFrame_OnEmptyStack_ReturnsNull()
    {
        var state = new IvrWorkflowState();

        Assert.Null(state.PopFrame());
        Assert.Equal(0, state.FrameDepth);
    }

    [Fact]
    public void MarkStepCompleted_IsGlobalAcrossFrames()
    {
        // Phase 0 contract: step completion remains workflow-global so existing guards
        // like PreviousStepCompletedGuard keep working. Phase 1+ may revisit if a subflow
        // legitimately needs to run twice in one call.
        var state = new IvrWorkflowState();
        state.PushFrame(new WorkflowFrame { WorkflowId = "parent", CurrentStepId = "welcome" });
        state.MarkStepCompleted("welcome");

        state.PushFrame(new WorkflowFrame { WorkflowId = "verify", CurrentStepId = "ask_pin" });

        Assert.True(state.IsStepCompleted("welcome"));
        Assert.Contains("welcome", state.CompletedSteps);

        state.PopFrame();

        Assert.True(state.IsStepCompleted("welcome"));
    }
}
