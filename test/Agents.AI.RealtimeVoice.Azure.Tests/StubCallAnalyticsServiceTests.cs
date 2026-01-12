using Agents.AI.RealtimeVoice.Azure.Calling;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;

namespace Agents.AI.RealtimeVoice.Azure.Tests;

/// <summary>
/// Tests for the StubCallAnalyticsService.
/// </summary>
public class StubCallAnalyticsServiceTests
{
    [Fact]
    public async Task AnalyzeUtterance_PositiveText_ReturnsPositiveSentiment()
    {
        var registry = new LiveCallRegistry();
        registry.Upsert(CreateTestCall("session-1"));
        var service = new StubCallAnalyticsService(registry);

        var result = await service.AnalyzeUtteranceAsync(
            "session-1",
            "user",
            "Thank you so much, this is great!");

        Assert.True(result.CustomerSentiment > 0);
    }

    [Fact]
    public async Task AnalyzeUtterance_NegativeText_ReturnsNegativeSentiment()
    {
        var registry = new LiveCallRegistry();
        registry.Upsert(CreateTestCall("session-1"));
        var service = new StubCallAnalyticsService(registry);

        var result = await service.AnalyzeUtteranceAsync(
            "session-1",
            "user",
            "I am so frustrated and angry about this terrible service!");

        Assert.True(result.CustomerSentiment < 0);
    }

    [Fact]
    public async Task AnalyzeUtterance_NeutralText_ReturnsNeutralSentiment()
    {
        var registry = new LiveCallRegistry();
        registry.Upsert(CreateTestCall("session-1"));
        var service = new StubCallAnalyticsService(registry);

        var result = await service.AnalyzeUtteranceAsync(
            "session-1",
            "user",
            "I would like to check my account balance please.");

        Assert.Equal(0, result.CustomerSentiment);
    }

    [Fact]
    public async Task AnalyzeUtterance_EscalationKeywords_HighEscalationRisk()
    {
        var registry = new LiveCallRegistry();
        registry.Upsert(CreateTestCall("session-1"));
        var service = new StubCallAnalyticsService(registry);

        var result = await service.AnalyzeUtteranceAsync(
            "session-1",
            "user",
            "I want to speak to your manager immediately! This is unacceptable!");

        Assert.True(result.EscalationRiskScore > 0.3);
    }

    [Fact]
    public async Task AnalyzeUtterance_CustomerSpeaker_UpdatesCustomerSentiment()
    {
        var registry = new LiveCallRegistry();
        registry.Upsert(CreateTestCall("session-1"));
        var service = new StubCallAnalyticsService(registry);

        var result = await service.AnalyzeUtteranceAsync(
            "session-1",
            "user",
            "Thanks, great service!");

        Assert.NotNull(result.CustomerSentiment);
        Assert.Null(result.AgentSentiment);
    }

    [Fact]
    public async Task AnalyzeUtterance_AgentSpeaker_UpdatesAgentSentiment()
    {
        var registry = new LiveCallRegistry();
        registry.Upsert(CreateTestCall("session-1"));
        var service = new StubCallAnalyticsService(registry);

        var result = await service.AnalyzeUtteranceAsync(
            "session-1",
            "assistant",
            "I am happy to help you with that.");

        Assert.NotNull(result.AgentSentiment);
        Assert.Null(result.CustomerSentiment);
    }

    [Fact]
    public async Task AnalyzeUtterance_UpdatesRegistry()
    {
        var registry = new LiveCallRegistry();
        registry.Upsert(CreateTestCall("session-1"));
        var service = new StubCallAnalyticsService(registry);

        await service.AnalyzeUtteranceAsync(
            "session-1",
            "user",
            "Thank you!");

        var call = registry.GetBySessionId("session-1");
        Assert.NotNull(call);
        Assert.NotNull(call.CustomerSentiment);
        Assert.NotNull(call.LatestUtteranceSummary);
    }

    [Fact]
    public async Task AnalyzeUtterance_LongText_TruncatesSummary()
    {
        var registry = new LiveCallRegistry();
        registry.Upsert(CreateTestCall("session-1"));
        var service = new StubCallAnalyticsService(registry);
        var longText = new string('a', 200);

        var result = await service.AnalyzeUtteranceAsync(
            "session-1",
            "user",
            longText);

        Assert.True(result.LatestUtteranceSummary!.Length <= 100);
        Assert.EndsWith("...", result.LatestUtteranceSummary);
    }

    [Fact]
    public async Task AnalyzeUtterance_StoresOriginalText()
    {
        var registry = new LiveCallRegistry();
        registry.Upsert(CreateTestCall("session-1"));
        var service = new StubCallAnalyticsService(registry);

        var result = await service.AnalyzeUtteranceAsync(
            "session-1",
            "user",
            "Original text here");

        Assert.Equal("Original text here", result.AnalyzedText);
    }

    [Fact]
    public async Task AnalyzeUtterance_StoresSpeaker()
    {
        var registry = new LiveCallRegistry();
        registry.Upsert(CreateTestCall("session-1"));
        var service = new StubCallAnalyticsService(registry);

        var result = await service.AnalyzeUtteranceAsync(
            "session-1",
            "customer",
            "Test");

        Assert.Equal("customer", result.Speaker);
    }

    [Fact]
    public async Task AnalyzeUtterance_MultipleAnalyses_BlendsSentiment()
    {
        var registry = new LiveCallRegistry();
        registry.Upsert(CreateTestCall("session-1"));
        var service = new StubCallAnalyticsService(registry);

        // First analysis - positive
        await service.AnalyzeUtteranceAsync("session-1", "user", "Great thanks!");
        var call1 = registry.GetBySessionId("session-1");
        var sentiment1 = call1!.CustomerSentiment;

        // Second analysis - negative
        await service.AnalyzeUtteranceAsync("session-1", "user", "This is terrible!");
        var call2 = registry.GetBySessionId("session-1");
        var sentiment2 = call2!.CustomerSentiment;

        // Sentiment should have changed (blended)
        Assert.NotEqual(sentiment1, sentiment2);
    }

    [Fact]
    public async Task AnalyzeUtterance_ThrowsOnNullSessionId()
    {
        var registry = new LiveCallRegistry();
        var service = new StubCallAnalyticsService(registry);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.AnalyzeUtteranceAsync(null!, "user", "text"));
    }

    [Fact]
    public async Task AnalyzeUtterance_ThrowsOnEmptyText()
    {
        var registry = new LiveCallRegistry();
        var service = new StubCallAnalyticsService(registry);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AnalyzeUtteranceAsync("session-1", "user", ""));
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
