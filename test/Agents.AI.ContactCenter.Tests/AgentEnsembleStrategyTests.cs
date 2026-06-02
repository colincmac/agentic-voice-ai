using System.Collections.Concurrent;
using System.Threading.Channels;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Core;
using Agents.AI.ContactCenter.Calling.Strategies.AgentEnsemble;
using Agents.AI.ContactCenter.Tests.Helpers;

namespace Agents.AI.RealtimeVoice.Azure.Tests.Proposed;

/// <summary>
/// Proves the new <see cref="AgentEnsembleStrategy"/>:
///   1. The active primary's audio reaches the strategy's outbound channel.
///   2. Caller audio is fanned to the active primary's backend.
///   3. Primary transcripts/utterances feed delegates via <c>OnContextAsync</c>.
///   4. Delegate insights surface as <see cref="StrategyEvent.DelegateInsight"/>.
///   5. <see cref="IAgentEnsemble.PromoteAsync"/> swaps the active speaker —
///      caller audio now goes to the new primary, <see cref="StrategyEvent.AgentSpeakingChanged"/>
///      fires, and outbound audio comes from the new backend.
/// </summary>
public class AgentEnsembleStrategyTests
{
    [Fact]
    public async Task Primary_audio_flows_and_delegates_emit_insights_from_transcripts()
    {
        var workflow = BuildWorkflow();

        var primary = new ControllableRealtimeBackend("primary", "Primary AI");
        var specialist = new ControllableRealtimeBackend("specialist", "Billing Specialist");

        var sentimentDelegate = new RecordingDelegate(
            "sentiment",
            "Sentiment Analyst",
            DelegateAgentRole.SentimentAnalyst,
            (ctx, write) =>
            {
                var newest = ctx.RecentTranscripts.LastOrDefault();
                if (newest is null)
                {
                    return ValueTask.CompletedTask;
                }
                return write(new AgentInsight(
                    "sentiment",
                    "sentiment",
                    Summary: $"score:negative for '{newest.Text}'",
                    Payload: -0.7,
                    Confidence: 0.9,
                    At: DateTimeOffset.UtcNow));
            });

        var researcherDelegate = new RecordingDelegate(
            "researcher",
            "CRM Researcher",
            DelegateAgentRole.Researcher,
            (ctx, write) =>
            {
                if (ctx.RecentTranscripts.Count == 0)
                {
                    return ValueTask.CompletedTask;
                }
                return write(new AgentInsight(
                    "researcher",
                    "lookup-result",
                    Summary: "Account #12345 located",
                    Payload: new { AccountId = "12345" },
                    Confidence: 1.0,
                    At: DateTimeOffset.UtcNow));
            });

        await using var ensemble = new DefaultAgentEnsemble(
            speakerCandidates: [
                new TestConversationalAgent("primary", "Primary AI", primary),
                new TestConversationalAgent("specialist", "Billing Specialist", specialist),
            ],
            delegates: [sentimentDelegate, researcherDelegate]);

        await using var strategy = new AgentEnsembleStrategy(ensemble, workflow);

        var inboundAudio = Channel.CreateUnbounded<AudioFrame>();
        var inboundDtmf = Channel.CreateUnbounded<DtmfTone>();

        await strategy.StartAsync(new StrategyStartContext
        {
            CallId = "call-ensemble",
            InboundAudio = inboundAudio.Reader,
            InboundDtmf = inboundDtmf.Reader,
            Services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(),
        });

        // Primary backend was connected and seeded with the system prompt.
        Assert.True(await primary.WaitForConnectAsync(TimeSpan.FromSeconds(2)));
        Assert.NotNull(primary.LastSystemPrompt);

        // Caller speaks → audio fans to the active primary's backend.
        await inboundAudio.Writer.WriteAsync(new AudioFrame(new byte[] { 0xAA, 0xBB }, DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => primary.ReceivedAudio.Count > 0, TimeSpan.FromSeconds(2));
        Assert.Single(primary.ReceivedAudio);
        Assert.Empty(specialist.ReceivedAudio);

        // Primary streams an audio frame back → strategy's outbound channel relays it.
        await primary.EmitAsync(new RealtimeBackendUpdate.Audio(new byte[] { 0x10, 0x20 }, DateTimeOffset.UtcNow));
        var firstOutbound = await ReadAudioAsync(strategy.Outbound, TimeSpan.FromSeconds(2));
        Assert.Equal(2, firstOutbound.Pcm.Length);
        Assert.Equal("primary", firstOutbound.SourceEdgeId);

        // Primary emits a final transcript → strategy fans it to delegates →
        // delegates push insights → strategy emits DelegateInsight events.
        await primary.EmitAsync(new RealtimeBackendUpdate.Transcript(
            "user", "I need help with my account", IsFinal: true, At: DateTimeOffset.UtcNow));

        var insights = await CollectEventsAsync<StrategyEvent.DelegateInsight>(
            strategy.Events,
            count: 2,
            timeout: TimeSpan.FromSeconds(3));

        Assert.Equal(2, insights.Count);
        Assert.Contains(insights, i => i.AgentId == "sentiment");
        Assert.Contains(insights, i => i.AgentId == "researcher");
        Assert.True(sentimentDelegate.InvocationCount > 0);
        Assert.True(researcherDelegate.InvocationCount > 0);

        await strategy.StopAsync();
    }

    [Fact]
    public async Task Promote_swaps_active_speaker_and_redirects_audio_pumps()
    {
        var workflow = BuildWorkflow();

        var primary = new ControllableRealtimeBackend("primary", "Primary AI");
        var specialist = new ControllableRealtimeBackend("specialist", "Billing Specialist");

        await using var ensemble = new DefaultAgentEnsemble(
            speakerCandidates: [
                new TestConversationalAgent("primary", "Primary AI", primary),
                new TestConversationalAgent("specialist", "Billing Specialist", specialist),
            ]);

        await using var strategy = new AgentEnsembleStrategy(ensemble, workflow);

        var inboundAudio = Channel.CreateUnbounded<AudioFrame>();
        var inboundDtmf = Channel.CreateUnbounded<DtmfTone>();

        await strategy.StartAsync(new StrategyStartContext
        {
            CallId = "call-promote",
            InboundAudio = inboundAudio.Reader,
            InboundDtmf = inboundDtmf.Reader,
            Services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(),
        });

        Assert.True(await primary.WaitForConnectAsync(TimeSpan.FromSeconds(2)));

        // Drain the initial AgentSpeakingChanged event so the next one we read is the
        // post-promotion one.
        var initialSpeaker = await ReadFirstMatchingEventAsync<StrategyEvent.AgentSpeakingChanged>(
            strategy.Events, TimeSpan.FromSeconds(2));
        Assert.Equal("primary", initialSpeaker.AgentId);

        // Promote specialist → strategy connects the new backend and re-pumps.
        await ensemble.PromoteAsync("specialist");

        Assert.True(await specialist.WaitForConnectAsync(TimeSpan.FromSeconds(2)));
        Assert.NotNull(specialist.LastSystemPrompt);

        var afterPromote = await ReadFirstMatchingEventAsync<StrategyEvent.AgentSpeakingChanged>(
            strategy.Events, TimeSpan.FromSeconds(2));
        Assert.Equal("specialist", afterPromote.AgentId);
        Assert.Equal("Billing Specialist", afterPromote.AgentDisplayName);

        // Caller audio now reaches the specialist, not the primary.
        await WaitUntilAsync(() =>
        {
            var primaryBefore = primary.ReceivedAudio.Count;
            return true;
        }, TimeSpan.FromMilliseconds(50));

        var primaryBaseline = primary.ReceivedAudio.Count;
        await inboundAudio.Writer.WriteAsync(new AudioFrame(new byte[] { 0xCC, 0xDD }, DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => specialist.ReceivedAudio.Count > 0, TimeSpan.FromSeconds(2));

        Assert.Single(specialist.ReceivedAudio);
        Assert.Equal(primaryBaseline, primary.ReceivedAudio.Count); // primary received nothing new

        // Specialist's audio is now what flows out of the strategy.
        await specialist.EmitAsync(new RealtimeBackendUpdate.Audio(new byte[] { 0x11, 0x22, 0x33 }, DateTimeOffset.UtcNow));
        var outboundFromSpecialist = await ReadAudioAsync(strategy.Outbound, TimeSpan.FromSeconds(2));
        Assert.Equal(3, outboundFromSpecialist.Pcm.Length);
        Assert.Equal("specialist", outboundFromSpecialist.SourceEdgeId);

        await strategy.StopAsync();
    }

    private static RealtimeIvrWorkflowDefinition BuildWorkflow() => new()
    {
        Name = "ensemble-test",
        BasePrompt = new RealtimePrompt(),
        Steps =
        [
            new RealtimeIvrWorkflowStep
            {
                Id = "greeting",
                ConversationState = new ConversationState
                {
                    Id = "greeting",
                    Description = "Help the caller",
                    Goal = "Resolve the caller's intent",
                    Instructions = ["Greet the caller and listen"]
                }
            }
        ]
    };

    private static async Task<AudioFrame> ReadOneAsync(ChannelReader<AudioFrame> reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await reader.ReadAsync(cts.Token);
    }

    private static async Task<AudioFrame> ReadAudioAsync(ChannelReader<OutboundDirective> reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var directive = await reader.ReadAsync(cts.Token);
        var audio = Assert.IsType<OutboundDirective.Audio>(directive);
        return audio.Frame;
    }

    private static async Task<List<T>> CollectEventsAsync<T>(
        ChannelReader<StrategyEvent> reader,
        int count,
        TimeSpan timeout) where T : StrategyEvent
    {
        var collected = new List<T>(count);
        using var cts = new CancellationTokenSource(timeout);
        await foreach (var ev in reader.ReadAllAsync(cts.Token))
        {
            if (ev is T match)
            {
                collected.Add(match);
                if (collected.Count >= count)
                {
                    return collected;
                }
            }
        }
        throw new TimeoutException($"Only collected {collected.Count}/{count} {typeof(T).Name} events");
    }

    private static async Task<T> ReadFirstMatchingEventAsync<T>(
        ChannelReader<StrategyEvent> reader,
        TimeSpan timeout) where T : StrategyEvent
    {
        using var cts = new CancellationTokenSource(timeout);
        await foreach (var ev in reader.ReadAllAsync(cts.Token))
        {
            if (ev is T match)
            {
                return match;
            }
        }
        throw new TimeoutException($"No {typeof(T).Name} event arrived within {timeout}");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("Predicate never satisfied");
    }
}

internal sealed class TestConversationalAgent : IConversationalAgent
{
    public TestConversationalAgent(string id, string display, IRealtimeVoiceBackend backend)
    {
        AgentId = id;
        DisplayName = display;
        Backend = backend;
    }

    public string AgentId { get; }
    public string DisplayName { get; }
    public IRealtimeVoiceBackend Backend { get; }
}

/// <summary>
/// Test delegate that runs a caller-supplied lambda each time it sees new context,
/// and records how many times it was invoked.
/// </summary>
internal sealed class RecordingDelegate : IDelegateAgent
{
    private readonly Func<EnsembleContext, Func<AgentInsight, ValueTask>, ValueTask> _onContext;
    private readonly ConcurrentDictionary<int, byte> _seenTranscriptHashes = new();
    private int _invocationCount;

    public RecordingDelegate(
        string id,
        string display,
        DelegateAgentRole role,
        Func<EnsembleContext, Func<AgentInsight, ValueTask>, ValueTask> onContext)
    {
        AgentId = id;
        DisplayName = display;
        Role = role;
        _onContext = onContext;
    }

    public string AgentId { get; }
    public string DisplayName { get; }
    public DelegateAgentRole Role { get; }

    public int InvocationCount => _invocationCount;

    public async ValueTask OnContextAsync(
        EnsembleContext context,
        ChannelWriter<AgentInsight> insights,
        CancellationToken cancellationToken = default)
    {
        // Dedupe by latest transcript hash so the polling loop doesn't fire the
        // delegate every 50ms. Production delegates would do their own dedup.
        var newest = context.RecentTranscripts.LastOrDefault();
        if (newest is null)
        {
            return;
        }
        var hash = HashCode.Combine(newest.Speaker, newest.Text);
        if (!_seenTranscriptHashes.TryAdd(hash, 0))
        {
            return;
        }

        Interlocked.Increment(ref _invocationCount);
        await _onContext(context, ins => insights.WriteAsync(ins, cancellationToken)).ConfigureAwait(false);
    }
}
