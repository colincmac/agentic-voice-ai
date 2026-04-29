using Agents.AI.RealtimeVoice.Azure.Calling;
using Agents.AI.RealtimeVoice.Azure.Models;

namespace Agents.AI.RealtimeVoice.Azure.Tests;

/// <summary>
/// Tests for the LiveCallRegistry and operator dashboard functionality.
/// </summary>
public class LiveCallRegistryTests
{
    [Fact]
    public void GetActiveCalls_WhenEmpty_ReturnsEmptyCollection()
    {
        var registry = new LiveCallRegistry();

        var result = registry.GetActiveCalls();

        Assert.Empty(result);
    }

    [Fact]
    public void Upsert_NewCall_AddsToRegistry()
    {
        var registry = new LiveCallRegistry();
        var call = CreateTestCall("session-1");

        registry.Upsert(call);

        var result = registry.GetActiveCalls();
        Assert.Single(result);
        Assert.Equal("session-1", result.First().SessionId);
    }

    [Fact]
    public void Upsert_ExistingCall_UpdatesCall()
    {
        var registry = new LiveCallRegistry();
        var call = CreateTestCall("session-1");
        registry.Upsert(call);

        call.CustomerSentiment = 0.75;
        registry.Upsert(call);

        var result = registry.GetBySessionId("session-1");
        Assert.NotNull(result);
        Assert.Equal(0.75, result.CustomerSentiment);
    }

    [Fact]
    public void GetBySessionId_ExistingCall_ReturnsClone()
    {
        var registry = new LiveCallRegistry();
        var call = CreateTestCall("session-1");
        registry.Upsert(call);

        var result1 = registry.GetBySessionId("session-1");
        var result2 = registry.GetBySessionId("session-1");

        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.NotSame(result1, result2);
    }

    [Fact]
    public void GetBySessionId_NonExistentCall_ReturnsNull()
    {
        var registry = new LiveCallRegistry();

        var result = registry.GetBySessionId("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public void EndSession_ExistingCall_UpdatesStatusAndEndTime()
    {
        var registry = new LiveCallRegistry();
        var call = CreateTestCall("session-1");
        registry.Upsert(call);
        var endTime = DateTimeOffset.UtcNow;

        var result = registry.EndSession("session-1", endTime);

        Assert.NotNull(result);
        Assert.Equal(LiveCallStatus.Ended, result.Status);
        Assert.Equal(endTime, result.EndedAt);
    }

    [Fact]
    public void EndSession_NonExistentCall_ReturnsNull()
    {
        var registry = new LiveCallRegistry();

        var result = registry.EndSession("nonexistent", DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void GetActiveCalls_ExcludesEndedCalls()
    {
        var registry = new LiveCallRegistry();
        var activeCall = CreateTestCall("session-active");
        var endedCall = CreateTestCall("session-ended");
        registry.Upsert(activeCall);
        registry.Upsert(endedCall);

        registry.EndSession("session-ended", DateTimeOffset.UtcNow);

        var result = registry.GetActiveCalls();
        Assert.Single(result);
        Assert.Equal("session-active", result.First().SessionId);
    }

    [Fact]
    public void Remove_ExistingCall_RemovesFromRegistry()
    {
        var registry = new LiveCallRegistry();
        var call = CreateTestCall("session-1");
        registry.Upsert(call);

        var removed = registry.Remove("session-1");

        Assert.True(removed);
        Assert.Null(registry.GetBySessionId("session-1"));
    }

    [Fact]
    public void Remove_NonExistentCall_ReturnsFalse()
    {
        var registry = new LiveCallRegistry();

        var removed = registry.Remove("nonexistent");

        Assert.False(removed);
    }

    [Fact]
    public void UpdateHealth_ExistingCall_AppliesUpdate()
    {
        var registry = new LiveCallRegistry();
        var call = CreateTestCall("session-1");
        registry.Upsert(call);

        var result = registry.UpdateHealth("session-1", c =>
        {
            c.CustomerSentiment = 0.5;
            c.EscalationRiskScore = 0.2;
            c.LatestUtteranceSummary = "Test utterance";
        });

        Assert.NotNull(result);
        Assert.Equal(0.5, result.CustomerSentiment);
        Assert.Equal(0.2, result.EscalationRiskScore);
        Assert.Equal("Test utterance", result.LatestUtteranceSummary);
    }

    [Fact]
    public void UpdateHealth_NonExistentCall_ReturnsNull()
    {
        var registry = new LiveCallRegistry();

        var result = registry.UpdateHealth("nonexistent", c => c.CustomerSentiment = 0.5);

        Assert.Null(result);
    }

    [Fact]
    public void CallStarted_Event_FiredOnNewCall()
    {
        var registry = new LiveCallRegistry();
        LiveCallSummary? eventCall = null;
        registry.CallStarted += (_, call) => eventCall = call;

        registry.Upsert(CreateTestCall("session-1"));

        Assert.NotNull(eventCall);
        Assert.Equal("session-1", eventCall.SessionId);
    }

    [Fact]
    public void CallEnded_Event_FiredOnEndSession()
    {
        var registry = new LiveCallRegistry();
        LiveCallSummary? eventCall = null;
        registry.Upsert(CreateTestCall("session-1"));
        registry.CallEnded += (_, call) => eventCall = call;

        registry.EndSession("session-1", DateTimeOffset.UtcNow);

        Assert.NotNull(eventCall);
        Assert.Equal("session-1", eventCall.SessionId);
        Assert.Equal(LiveCallStatus.Ended, eventCall.Status);
    }

    [Fact]
    public void CallHealthUpdated_Event_FiredOnUpdateHealth()
    {
        var registry = new LiveCallRegistry();
        LiveCallSummary? eventCall = null;
        registry.Upsert(CreateTestCall("session-1"));
        registry.CallHealthUpdated += (_, call) => eventCall = call;

        registry.UpdateHealth("session-1", c => c.CustomerSentiment = 0.5);

        Assert.NotNull(eventCall);
        Assert.Equal("session-1", eventCall.SessionId);
        Assert.Equal(0.5, eventCall.CustomerSentiment);
    }

    [Fact]
    public void MultipleCalls_TrackedIndependently()
    {
        var registry = new LiveCallRegistry();

        registry.Upsert(CreateTestCall("session-1"));
        registry.Upsert(CreateTestCall("session-2"));
        registry.Upsert(CreateTestCall("session-3"));

        var activeCalls = registry.GetActiveCalls();
        Assert.Equal(3, activeCalls.Count);
    }

    [Fact]
    public void Duration_ComputedCorrectly()
    {
        var startTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var call = new LiveCallSummary
        {
            SessionId = "session-1",
            StartedAt = startTime
        };

        Assert.True(call.Duration >= TimeSpan.FromMinutes(5));
    }

    private static LiveCallSummary CreateTestCall(string sessionId)
    {
        return new LiveCallSummary
        {
            SessionId = sessionId,
            StartedAt = DateTimeOffset.UtcNow,
            Status = LiveCallStatus.Active,
            Participants = []
        };
    }
}
