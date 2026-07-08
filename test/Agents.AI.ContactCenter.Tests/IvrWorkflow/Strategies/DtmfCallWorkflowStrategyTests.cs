using System.Runtime.CompilerServices;
using System.Threading.Channels;
using global::Agents.AI.ContactCenter.Calling;
using global::Agents.AI.ContactCenter.Calling.Strategies.Dtmf;
using global::Agents.AI.ContactCenter.IvrWorkflow;
using global::Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using global::Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using global::Agents.AI.ContactCenter.IvrWorkflow.Execution;
using global::Agents.AI.ContactCenter.Media.Audio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Strategies;

public sealed class DtmfCallWorkflowStrategyTests
{
    private static CompiledCallWorkflow MenuWorkflow() => new WorkflowGraphCompiler().Compile(new WorkflowBlueprint
    {
        Id = "menu",
        InitialStageId = "welcome",
        Stages =
        [
            new StageBlueprint
            {
                Id = "welcome",
                Channels = new StageChannelConfig
                {
                    Scripted = new StageScriptedConfig
                    {
                        SsmlPrompt = "Press 1 for balance, 2 for transfer.",
                        MenuOptions = new Dictionary<char, ScriptedMenuOption>
                        {
                            ['1'] = new ScriptedMenuOption("balance", "balance"),
                            ['2'] = new ScriptedMenuOption("transfer", "agent"),
                        },
                    },
                },
                Transitions =
                [
                    new TransitionBlueprint { TargetStageId = "balance", Label = "balance" },
                    new TransitionBlueprint { TargetStageId = "transfer", Label = "agent" },
                ],
            },
            new StageBlueprint { Id = "balance", Terminal = true },
            new StageBlueprint { Id = "transfer", Terminal = true },
        ],
    });

    private static StrategyStartContext NewStartContext(
        Channel<DtmfTone> dtmf,
        IServiceProvider services) => new()
        {
            CallId = "call-1",
            InboundAudio = Channel.CreateUnbounded<AudioFrame>().Reader,
            InboundDtmf = dtmf.Reader,
            Services = services,
        };

    [Fact]
    public async Task Start_RendersInitialStage()
    {
        var workflow = MenuWorkflow();
        var sp = new ServiceCollection().BuildServiceProvider();
        var session = new CallWorkflowSession(workflow, new IvrWorkflowState(), sp);
        var synthesizer = new RecordingSynthesizer();

        await using var strategy = new DtmfCallWorkflowStrategy(session, synthesizer, NullLoggerFactory.Instance);
        var dtmf = Channel.CreateUnbounded<DtmfTone>();

        await strategy.StartAsync(NewStartContext(dtmf, sp));

        var entered = await ReadOneAsync(strategy.Events, e => e is StrategyEvent.WorkflowStepEntered);
        Assert.IsType<StrategyEvent.WorkflowStepEntered>(entered);
        Assert.Equal("welcome", ((StrategyEvent.WorkflowStepEntered)entered).StepId);

        // Synthesis is asynchronous — wait for the AgentUtterance event emitted after
        // SynthesizeAsync completes before asserting on the recorded texts.
        var utterance = await ReadOneAsync(strategy.Events, e => e is StrategyEvent.AgentUtterance);
        Assert.Equal("Press 1 for balance, 2 for transfer.", ((StrategyEvent.AgentUtterance)utterance).Text);
        Assert.Equal(["Press 1 for balance, 2 for transfer."], synthesizer.SynthesizedTexts);
    }

    [Fact]
    public async Task DigitMappedToTransition_AdvancesStage()
    {
        var workflow = MenuWorkflow();
        var sp = new ServiceCollection().BuildServiceProvider();
        var session = new CallWorkflowSession(workflow, new IvrWorkflowState(), sp);

        await using var strategy = new DtmfCallWorkflowStrategy(session, synthesizer: null, NullLoggerFactory.Instance);
        var dtmf = Channel.CreateUnbounded<DtmfTone>();

        await strategy.StartAsync(NewStartContext(dtmf, sp));
        // Wait for initial stage to be entered before sending DTMF (avoids racing the executor).
        await ReadOneAsync(strategy.Events, e => e is StrategyEvent.WorkflowStepEntered);

        await dtmf.Writer.WriteAsync(new DtmfTone('1', DateTimeOffset.UtcNow));

        var entered = (StrategyEvent.WorkflowStepEntered)await ReadOneAsync(
            strategy.Events,
            e => e is StrategyEvent.WorkflowStepEntered we && we.StepId == "balance");
        Assert.Equal("balance", entered.StepId);
        Assert.Equal("balance", session.Navigator.CurrentStage!.Id);

        dtmf.Writer.Complete();
    }

    [Fact]
    public async Task UnmappedDigit_IsIgnored()
    {
        var workflow = MenuWorkflow();
        var sp = new ServiceCollection().BuildServiceProvider();
        var session = new CallWorkflowSession(workflow, new IvrWorkflowState(), sp);

        await using var strategy = new DtmfCallWorkflowStrategy(session, synthesizer: null, NullLoggerFactory.Instance);
        var dtmf = Channel.CreateUnbounded<DtmfTone>();

        await strategy.StartAsync(NewStartContext(dtmf, sp));
        await ReadOneAsync(strategy.Events, e => e is StrategyEvent.WorkflowStepEntered);

        await dtmf.Writer.WriteAsync(new DtmfTone('9', DateTimeOffset.UtcNow));

        // DtmfRecognized fires for every digit, but the unmapped one should NOT trigger another stage entry.
        var recognized = await ReadOneAsync(strategy.Events, e => e is StrategyEvent.DtmfRecognized);
        Assert.IsType<StrategyEvent.DtmfRecognized>(recognized);

        // Give the executor a chance to (incorrectly) advance.
        await Task.Delay(50);
        Assert.Equal("welcome", session.Navigator.CurrentStage!.Id);

        dtmf.Writer.Complete();
    }

    private static async Task<StrategyEvent> ReadOneAsync(
        ChannelReader<StrategyEvent> reader,
        Func<StrategyEvent, bool> predicate)
    {
        await foreach (var evt in reader.ReadAllAsync())
        {
            if (predicate(evt))
            {
                return evt;
            }
        }
        throw new InvalidOperationException("Channel completed without matching event.");
    }

    private sealed class RecordingSynthesizer : ISpeechSynthesizer
    {
        public List<string> SynthesizedTexts { get; } = [];

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
            string text,
            SynthesizerInputFormat inputFormat = SynthesizerInputFormat.Text,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SynthesizedTexts.Add(text);
            yield return new byte[] { 0, 0 };
            await Task.CompletedTask;
        }
    }
}
