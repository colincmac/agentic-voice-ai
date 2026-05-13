using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.LiveVoice.Media.Audio;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Agents.AI.RealtimeVoice.Azure.Calling.Proposed;
using Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Implementation;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.RealtimeVoice.Azure.Tests.Proposed;

/// <summary>
/// Proves the new <see cref="CompositeFallbackStrategy"/> degrades from the
/// realtime voice tier to DTMF mid-call when the realtime backend faults,
/// preserves <see cref="IvrWorkflowState"/> across the swap, and emits
/// <see cref="StrategyEvent.TierDegraded"/> for the dashboard.
/// </summary>
public class CompositeFallbackStrategyTests
{
    [Fact]
    public async Task Realtime_to_dtmf_fallback_preserves_workflow_state_and_continues_call()
    {
        var workflow = BuildWorkflow();

        // Backend we can fault on demand.
        var backend = new ControllableRealtimeBackend(agentId: "primary-agent", agentDisplayName: "Primary AI");

        var services = new ServiceCollection()
            .AddSingleton<ISpeechSynthesizer, FakeSpeechSynthesizer>()
            .AddSingleton<IRealtimeVoiceBackend>(backend)
            .BuildServiceProvider();

        var quality = new InMemoryCallQualityReporter();
        var registry = new CallSessionRegistry();
        var fakeEdge = new FakeCallerEdge("call-fb");

        // The composite — realtime first, DTMF underneath — is built by a tiny
        // wrapper factory so CallSessionFactory's tier-keyed dispatch still works.
        var realtimeFactory = new RealtimeVoiceStrategyFactory();
        var dtmfFactory = new DtmfStreamingStrategyFactory();
        var compositeFactory = new CompositeStrategyFactoryAdapter(
            tier: AgentTier.RealtimeVoice,
            inner: [realtimeFactory, dtmfFactory]);

        var sessionFactory = new CallSessionFactory(
            services.GetRequiredService<IServiceScopeFactory>(),
            [compositeFactory],
            registry,
            quality,
            defaultObservers: [new DashboardProjectionObserver()]);

        var session = await sessionFactory.CreateAsync(new CallSessionRequest
        {
            CallId = "call-fb",
            CallerEdge = fakeEdge,
            Workflow = workflow,
            PreferredTier = AgentTier.RealtimeVoice
        });

        var snapshots = quality.Subscribe("call-fb");

        await session.StartAsync();

        // 1. The realtime backend was started and the prompt for the initial step was applied.
        Assert.True(await backend.WaitForConnectAsync(TimeSpan.FromSeconds(2)));
        Assert.NotNull(backend.LastSystemPrompt);

        // 2. Stream a frame from the realtime model — caller should hear it.
        await backend.EmitAsync(new RealtimeBackendUpdate.Audio(
            new byte[] { 0x01, 0x02, 0x03, 0x04 },
            DateTimeOffset.UtcNow));

        var realtimeFrame = await ReadOneAsync(fakeEdge.Sent.Reader, TimeSpan.FromSeconds(2));
        Assert.Equal(4, realtimeFrame.Pcm.Length);

        // 3. Caller picks billing while realtime is still active — workflow state advances.
        //    (Realtime backend is responsible for parsing intent in production; here we
        //    drive workflow state directly to model "the AI moved the caller forward".)
        session.Strategy.WorkflowState.MarkStepCompleted("greeting");
        session.Strategy.WorkflowState.CurrentStepName = "billing";
        session.Strategy.WorkflowState.Set("greeting_selection", "billing");

        // 4. Force a fault — WebSocket dropped, model API blew up, whatever.
        await backend.FaultAsync(new InvalidOperationException("realtime websocket dropped"));

        // 5. Composite should swap to DTMF and emit TierDegraded.
        //    The dashboard observer publishes the tier change and the alert as separate
        //    updates, so wait for the snapshot that has both.
        var degradedSnap = await WaitForAsync(
            snapshots,
            s => s.ActiveTier == AgentTier.DtmfOnly
                 && s.Alerts.Any(a => a.Kind == QualityAlertKind.TierDegraded),
            TimeSpan.FromSeconds(3));

        Assert.Equal(AgentTier.DtmfOnly, degradedSnap.ActiveTier);
        Assert.Contains(degradedSnap.Alerts, a => a.Kind == QualityAlertKind.TierDegraded);

        // 6. The DTMF strategy resumed at the SAME workflow step — no greeting replay.
        Assert.Equal("billing", session.Strategy.WorkflowState.CurrentStepName);
        Assert.Contains("greeting", session.Strategy.WorkflowState.CompletedSteps);
        Assert.Equal("billing", session.Strategy.WorkflowState.Get<string>("greeting_selection"));

        // 7. DTMF prompt audio should be flowing now (TTS synth).
        var dtmfFrame = await ReadOneAsync(fakeEdge.Sent.Reader, TimeSpan.FromSeconds(2));
        Assert.NotEqual(0, dtmfFrame.Pcm.Length);

        // 8. Caller can still drive the IVR through the new strategy.
        await fakeEdge.PushDtmfAsync('#'); // billing has no menu — we just verify a digit lands without throwing.

        // 9. Caller hangs up — session ends cleanly even after a mid-call swap.
        await fakeEdge.HangupAsync();
        await WaitUntilAsync(() => session.State == CallSessionState.Ended, TimeSpan.FromSeconds(2));
        Assert.Equal(CallSessionState.Ended, session.State);
    }

    private static RealtimeIvrWorkflowDefinition BuildWorkflow() => new()
    {
        Name = "fallback-test-ivr",
        BasePrompt = new RealtimePrompt(),
        Steps =
        [
            new RealtimeIvrWorkflowStep
            {
                Id = "greeting",
                ConversationState = new ConversationState
                {
                    Id = "greeting",
                    Description = "Welcome to Contoso",
                    Goal = "Route the caller",
                    Instructions = ["Greet the caller"],
                    Transitions = [new StateTransition { NextStep = "billing", Condition = "billing" }]
                },
                StepDtmfConfiguration = new StepDtmfConfiguration(options: new Dictionary<char, string> { ['1'] = "support", ['2'] = "billing" })
            },
            new RealtimeIvrWorkflowStep
            {
                Id = "billing",
                ConversationState = new ConversationState
                {
                    Id = "billing",
                    Description = "Billing department",
                    Instructions = ["Help with billing"]
                }
            }
        ]
    };

    private static async Task<AudioFrame> ReadOneAsync(ChannelReader<AudioFrame> reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await reader.ReadAsync(cts.Token);
    }

    private static async Task<CallQualitySnapshot> WaitForAsync(
        ChannelReader<CallQualitySnapshot> reader,
        Func<CallQualitySnapshot, bool> predicate,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await foreach (var snap in reader.ReadAllAsync(cts.Token))
        {
            if (predicate(snap))
            {
                return snap;
            }
        }
        throw new TimeoutException("Snapshot predicate never matched");
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

/// <summary>
/// Test-only adapter that exposes a <see cref="CompositeFallbackStrategy"/>
/// behind the single-tier <see cref="IConversationStrategyFactory"/> contract,
/// so <see cref="CallSessionFactory"/> can dispatch to it by tier key.
/// </summary>
internal sealed class CompositeStrategyFactoryAdapter : IConversationStrategyFactory
{
    private readonly IReadOnlyList<IConversationStrategyFactory> _inner;

    public CompositeStrategyFactoryAdapter(AgentTier tier, IReadOnlyList<IConversationStrategyFactory> inner)
    {
        Tier = tier;
        _inner = inner;
    }

    public AgentTier Tier { get; }

    public ValueTask<IConversationStrategy> CreateAsync(
        string callId,
        IServiceProvider services,
        RealtimeIvrWorkflowDefinition workflow,
        IvrWorkflowState? restoreFrom,
        CancellationToken cancellationToken = default)
    {
        IConversationStrategy strategy = new CompositeFallbackStrategy(_inner, workflow);
        return ValueTask.FromResult(strategy);
    }
}

/// <summary>
/// Test double for <see cref="IRealtimeVoiceBackend"/>. Exposes async hooks so
/// the test can stream audio updates and inject faults at deterministic moments.
/// </summary>
internal sealed class ControllableRealtimeBackend : IRealtimeVoiceBackend
{
    private readonly Channel<RealtimeBackendUpdate> _updates = Channel.CreateUnbounded<RealtimeBackendUpdate>();
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ControllableRealtimeBackend(string agentId, string agentDisplayName)
    {
        AgentId = agentId;
        AgentDisplayName = agentDisplayName;
    }

    public string AgentId { get; }
    public string AgentDisplayName { get; }
    public string? LastSystemPrompt { get; private set; }
    public List<ReadOnlyMemory<byte>> ReceivedAudio { get; } = [];

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _connected.TrySetResult();
        return Task.CompletedTask;
    }

    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default)
    {
        ReceivedAudio.Add(pcm);
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateSystemPromptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        LastSystemPrompt = prompt;
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<RealtimeBackendUpdate> RunAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public ValueTask EmitAsync(RealtimeBackendUpdate update)
        => _updates.Writer.WriteAsync(update);

    public ValueTask FaultAsync(Exception ex)
        => _updates.Writer.WriteAsync(new RealtimeBackendUpdate.Faulted(ex, ex.Message, DateTimeOffset.UtcNow));

    public async Task<bool> WaitForConnectAsync(TimeSpan timeout)
    {
        var completed = await Task.WhenAny(_connected.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return completed == _connected.Task;
    }

    public ValueTask DisposeAsync()
    {
        _updates.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
