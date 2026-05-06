using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.LiveVoice.Media.Audio;
using Agents.AI.Extensions.LiveVoice.Media.Signaling;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Agents.AI.RealtimeVoice.Azure.Calling.Proposed;
using Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Implementation;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.RealtimeVoice.Azure.Tests.Proposed;

/// <summary>
/// End-to-end proof for the new ICallEdge / IConversationStrategy / ICallSession contracts
/// with the DTMF strategy. Drives a fake caller edge and asserts that:
///   1. the strategy plays the initial step prompt as outbound audio,
///   2. inbound DTMF advances the workflow to the next step,
///   3. StrategyEvents are projected onto the dashboard quality snapshot,
///   4. caller hangup tears the session down cleanly.
/// </summary>
public class DtmfCallSessionEndToEndTests
{
    [Fact]
    public async Task Caller_navigates_dtmf_menu_and_hangs_up()
    {
        // Arrange: a tiny two-step workflow — greeting → billing.
        var workflow = BuildWorkflow();

        var services = new ServiceCollection()
            .AddSingleton<ISpeechSynthesizer, FakeSpeechSynthesizer>()
            .BuildServiceProvider();

        await using var scope = services.CreateAsyncScope();
        var quality = new InMemoryCallQualityReporter();
        var registry = new CallSessionRegistry();
        var factory = new CallSessionFactory(
            services.GetRequiredService<IServiceScopeFactory>(),
            [new DtmfStrategyFactory()],
            registry,
            quality,
            defaultObservers: [new DashboardProjectionObserver()]);

        var fakeEdge = new FakeCallerEdge("call-1");

        // Act: answer the call.
        var session = await factory.CreateAsync(new CallSessionRequest
        {
            CallId = "call-1",
            CallerEdge = fakeEdge,
            Workflow = workflow,
            PreferredTier = AgentTier.DtmfOnly,
        });

        var snapshots = quality.Subscribe("call-1");

        await session.StartAsync();

        // Initial step should produce TTS audio for the greeting prompt.
        var firstFrame = await ReadOneAsync(fakeEdge.Sent.Reader, TimeSpan.FromSeconds(2));
        Assert.NotEqual(0, firstFrame.Pcm.Length);

        // Send DTMF "2" — should map to billing transition.
        await fakeEdge.PushDtmfAsync('2');

        // Wait until the dashboard reflects the new step.
        var stepSnap = await WaitForAsync(snapshots, s => s.CurrentWorkflowStep == "billing");
        Assert.Equal("billing", stepSnap.CurrentWorkflowStep);
        Assert.NotNull(stepSnap.LatestAgentUtterance);
        Assert.Equal(StrategyKind.Dtmf, stepSnap.StrategyKind);
        Assert.Equal(AgentTier.DtmfOnly, stepSnap.ActiveTier);

        // Workflow state was advanced and the previous step was marked complete.
        Assert.Equal("billing", session.Strategy.WorkflowState.CurrentStepName);
        Assert.Contains("greeting", session.Strategy.WorkflowState.CompletedSteps);
        Assert.Equal("billing", session.Strategy.WorkflowState.Get<string>("greeting_selection"));

        // Caller hangs up — session should reach Ended on its own.
        await fakeEdge.HangupAsync();

        await WaitUntilAsync(() => session.State == CallSessionState.Ended, TimeSpan.FromSeconds(2));
        Assert.Equal(CallSessionState.Ended, session.State);
        Assert.Null(registry.TryGet("call-1"));
    }

    [Fact]
    public async Task Strategy_swap_preserves_workflow_state()
    {
        var workflow = BuildWorkflow();

        var services = new ServiceCollection()
            .AddSingleton<ISpeechSynthesizer, FakeSpeechSynthesizer>()
            .BuildServiceProvider();

        var quality = new InMemoryCallQualityReporter();
        var registry = new CallSessionRegistry();
        var factory = new CallSessionFactory(
            services.GetRequiredService<IServiceScopeFactory>(),
            [new DtmfStrategyFactory()],
            registry,
            quality,
            defaultObservers: []);

        var fakeEdge = new FakeCallerEdge("call-2");

        var session = await factory.CreateAsync(new CallSessionRequest
        {
            CallId = "call-2",
            CallerEdge = fakeEdge,
            Workflow = workflow,
            PreferredTier = AgentTier.DtmfOnly,
        });

        await session.StartAsync();

        // Drain the initial prompt audio.
        _ = await ReadOneAsync(fakeEdge.Sent.Reader, TimeSpan.FromSeconds(2));

        // Drive into "billing".
        await fakeEdge.PushDtmfAsync('2');
        await WaitUntilAsync(
            () => session.Strategy.WorkflowState.CurrentStepName == "billing",
            TimeSpan.FromSeconds(2));

        var previousState = session.Strategy.WorkflowState;

        // Build a replacement strategy seeded from the previous workflow state.
        var replacement = new DtmfStrategy(
            workflow,
            services.GetRequiredService<ISpeechSynthesizer>(),
            restoreFrom: previousState);

        var swapped = await session.ReplaceStrategyAsync(replacement);

        Assert.True(swapped);
        Assert.Equal("billing", session.Strategy.WorkflowState.CurrentStepName);
        Assert.Contains("greeting", session.Strategy.WorkflowState.CompletedSteps);
        Assert.Equal("billing", session.Strategy.WorkflowState.Get<string>("greeting_selection"));

        await session.EndAsync();
    }

    private static RealtimeIvrWorkflowDefinition BuildWorkflow() => new()
    {
        Name = "test-ivr",
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
                    Instructions = ["Greet the caller and offer menu"],
                    Transitions =
                    [
                        new StateTransition { NextStep = "billing", Condition = "selected billing" }
                    ]
                },
                DtmfMenuOptions = new Dictionary<char, string>
                {
                    ['1'] = "support",
                    ['2'] = "billing"
                }
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
        TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
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

internal sealed class FakeCallerEdge : ICallEdge
{
    private readonly Channel<AudioFrame> _inboundAudio = Channel.CreateUnbounded<AudioFrame>();
    private readonly Channel<DtmfTone> _inboundDtmf = Channel.CreateUnbounded<DtmfTone>();
    private readonly Channel<SessionSignal> _inboundSignals = Channel.CreateUnbounded<SessionSignal>();
    private bool _connected;
    private int _disconnectFired;

    public FakeCallerEdge(string edgeId)
    {
        EdgeId = edgeId;
        Metadata = new CallEdgeMetadata
        {
            DisplayName = "fake-caller",
            RawIdentifier = edgeId
        };
    }

    public string EdgeId { get; }
    public CallEdgeKind Kind => CallEdgeKind.Caller;
    public CallEdgeMetadata Metadata { get; }
    public bool IsConnected => _connected;

    public ChannelReader<AudioFrame> InboundAudio => _inboundAudio.Reader;
    public ChannelReader<DtmfTone> InboundDtmf => _inboundDtmf.Reader;
    public ChannelReader<SessionSignal> InboundSignals => _inboundSignals.Reader;

    public Channel<AudioFrame> Sent { get; } = Channel.CreateUnbounded<AudioFrame>();

    public event Func<EdgeDisconnectedReason, ValueTask>? Disconnected;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    public ValueTask SendAudioAsync(AudioFrame frame, CancellationToken cancellationToken = default)
        => Sent.Writer.WriteAsync(frame, cancellationToken);

    public ValueTask StopAudioAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask PushDtmfAsync(char digit)
        => _inboundDtmf.Writer.WriteAsync(new DtmfTone(digit, DateTimeOffset.UtcNow));

    public async Task HangupAsync()
    {
        _connected = false;
        _inboundAudio.Writer.TryComplete();
        _inboundDtmf.Writer.TryComplete();
        _inboundSignals.Writer.TryComplete();

        if (Interlocked.Exchange(ref _disconnectFired, 1) != 0)
        {
            return;
        }

        var handlers = Disconnected;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Func<EdgeDisconnectedReason, ValueTask>>())
        {
            await handler(EdgeDisconnectedReason.CallerHangup);
        }
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        _inboundAudio.Writer.TryComplete();
        _inboundDtmf.Writer.TryComplete();
        _inboundSignals.Writer.TryComplete();
        Sent.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeSpeechSynthesizer : ISpeechSynthesizer
{
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Deterministic non-empty PCM payload so audio assertions are simple.
        yield return new byte[] { 0x10, 0x20, 0x30, 0x40 };
        await Task.Yield();
    }
}
