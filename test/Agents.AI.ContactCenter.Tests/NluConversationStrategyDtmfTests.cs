using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Agents.IntentAgent;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Strategies;
using Agents.AI.ContactCenter.Calling.Strategies.Nlu;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Media.Transcription;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests;

/// <summary>
/// Covers the in-strategy DTMF shortcut added to <see cref="NluConversationStrategy"/>:
/// pressing a digit that maps to a <c>scripted.dtmf.options</c> entry must drive the
/// matching transition WITHOUT invoking the speech classifier, while an unmapped digit
/// must be silently ignored and leave the classifier as the sole intent source.
/// </summary>
public class NluConversationStrategyDtmfTests
{
    [Fact]
    public async Task Menu_digit_transitions_stage_without_invoking_chat_classifier()
    {
        var chatInvocations = 0;
        var chat = new ThrowingChatClient(() => Interlocked.Increment(ref chatInvocations));
        var intentAgent = new IvrIntentAgent(chat, new NoOpSpeechRecognizer());

        var workflow = BuildMenuWorkflow();
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var strategy = new NluConversationStrategy(
            IvrWorkflowSession.Create(workflow, services),
            intentAgent,
            new RecordingSpeechSynthesizer(),
            escalationTarget: null);

        var (dtmfWriter, ctx) = await StartAsync(strategy);

        try
        {
            // Wait for the strategy's classifier loop to enter the initial step before pressing
            // any digits — the DTMF pump silently no-ops while the navigator is still null.
            await WaitForEventAsync<StrategyEvent.WorkflowStepEntered>(
                strategy.Events, e => e.StepId == "welcome", TimeSpan.FromSeconds(2));

            // Press '2' → 'billing' per the menu mapping below.
            await dtmfWriter.WriteAsync(new DtmfTone('2', DateTimeOffset.UtcNow));

            var entered = await WaitForEventAsync<StrategyEvent.WorkflowStepEntered>(
                strategy.Events, e => e.StepId == "billing", TimeSpan.FromSeconds(2));

            Assert.Equal("billing", entered.StepId);

            // No chat-classifier roundtrip should have occurred for a DTMF-resolved stage.
            Assert.Equal(0, chatInvocations);
        }
        finally
        {
            await strategy.StopAsync();
            await strategy.DisposeAsync();
        }
    }

    [Fact]
    public async Task Unmapped_digit_is_ignored_and_does_not_transition()
    {
        var chat = new ThrowingChatClient(() => { /* never invoked in this test either */ });
        var intentAgent = new IvrIntentAgent(chat, new NoOpSpeechRecognizer());

        var workflow = BuildMenuWorkflow();
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var strategy = new NluConversationStrategy(
            IvrWorkflowSession.Create(workflow, services),
            intentAgent,
            new RecordingSpeechSynthesizer(),
            escalationTarget: null);

        var (dtmfWriter, ctx) = await StartAsync(strategy);

        try
        {
            // Wait for the initial step entry before exercising the DTMF pump.
            await WaitForEventAsync<StrategyEvent.WorkflowStepEntered>(
                strategy.Events, e => e.StepId == "welcome", TimeSpan.FromSeconds(2));

            // '7' is not in the menu and not a stage-scoped intent name → must be ignored.
            await dtmfWriter.WriteAsync(new DtmfTone('7', DateTimeOffset.UtcNow));

            // The DtmfRecognized event always fires for observability.
            var recognised = await WaitForEventAsync<StrategyEvent.DtmfRecognized>(
                strategy.Events, _ => true, TimeSpan.FromSeconds(2));
            Assert.Equal("7", recognised.Digits);

            // Give the pump a moment to (not) transition.
            await Task.Delay(150);
            Assert.Equal("welcome", strategy.WorkflowState.CurrentStepName);
        }
        finally
        {
            await strategy.StopAsync();
            await strategy.DisposeAsync();
        }
    }

    // ---------- helpers ----------

    private static async Task<(ChannelWriter<DtmfTone> dtmfWriter, StrategyStartContext ctx)>
        StartAsync(NluConversationStrategy strategy)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var audio = Channel.CreateUnbounded<AudioFrame>();
        var dtmf = Channel.CreateUnbounded<DtmfTone>();

        var ctx = new StrategyStartContext
        {
            CallId = "call-nlu-dtmf",
            InboundAudio = audio.Reader,
            InboundDtmf = dtmf.Reader,
            Services = services,
            CallerMetadata = null,
        };

        await strategy.StartAsync(ctx);
        return (dtmf.Writer, ctx);
    }

    private static async Task<T> WaitForEventAsync<T>(
        ChannelReader<StrategyEvent> reader,
        Func<T, bool> predicate,
        TimeSpan timeout) where T : StrategyEvent
    {
        using var cts = new CancellationTokenSource(timeout);
        await foreach (var evt in reader.ReadAllAsync(cts.Token))
        {
            if (evt is T typed && predicate(typed))
            {
                return typed;
            }
        }
        throw new TimeoutException($"Did not observe a {typeof(T).Name} matching the predicate.");
    }

    private static RealtimeIvrWorkflowDefinition BuildMenuWorkflow() => new()
    {
        Name = "nlu-dtmf-shortcut",
        BasePrompt = new RealtimePrompt(),
        Steps =
        [
            new RealtimeIvrWorkflowStep
            {
                Id = "welcome",
                ConversationState = new ConversationState
                {
                    Id = "welcome",
                    Description = "Welcome",
                    Instructions = ["Greet the caller"],
                    Transitions =
                    [
                        new StateTransition { NextStep = "support", Condition = "dtmf-1" },
                        new StateTransition { NextStep = "billing", Condition = "dtmf-2" },
                    ]
                },
                StepScriptedConfiguration = new StepScriptedConfiguration
                {
                    Dtmf = new StepDtmfConfiguration
                    {
                        MenuOptions = new Dictionary<char, DtmfMenuOption>
                        {
                            ['1'] = new() { Digit = '1', Label = "support", NextStepId = "support" },
                            ['2'] = new() { Digit = '2', Label = "billing", NextStepId = "billing" },
                        }
                    }
                }
            },
            new RealtimeIvrWorkflowStep
            {
                Id = "support",
                ConversationState = new ConversationState { Id = "support", Description = "Support", Instructions = ["Help"] }
            },
            new RealtimeIvrWorkflowStep
            {
                Id = "billing",
                ConversationState = new ConversationState { Id = "billing", Description = "Billing", Instructions = ["Help"] }
            }
        ]
    };

    private sealed class ThrowingChatClient : IChatClient
    {
        private readonly Action _onCall;
        public ThrowingChatClient(Action onCall) { _onCall = onCall; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _onCall();
            throw new InvalidOperationException(
                "Chat classifier must not be invoked when DTMF resolves the stage.");
        }

#pragma warning disable CS1998
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _onCall();
            throw new InvalidOperationException(
                "Streaming chat classifier must not be invoked when DTMF resolves the stage.");
        }
#pragma warning restore CS1998

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NoOpSpeechRecognizer : ISpeechRecognizer
    {
        public Task WriteAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<TranscriptSegment> GetTranscriptsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Block forever (until cancellation) without producing any transcripts.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            yield break;
        }
#pragma warning restore CS1998

        public Task CompleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingSpeechSynthesizer : ISpeechSynthesizer
    {
#pragma warning disable CS1998
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
            string text,
            SynthesizerInputFormat format,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new byte[] { 0x01 };
        }
#pragma warning restore CS1998

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
