using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Strategies;
using Agents.AI.ContactCenter.Calling.Strategies.RealtimeVoice;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Agents.AI.RealtimeVoice.Azure.Tests.Proposed; // ControllableRealtimeBackend
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests;

/// <summary>
/// Covers the DTMF-input handling added to <see cref="RealtimeVoiceStrategy"/>:
/// <list type="bullet">
///   <item>Stage-aware menu: digit routes to the matching <c>scripted.dtmf.options</c> next-stage transition.</item>
///   <item>LLM-aware fallback: digit on a stage without scripted DTMF config is forwarded as a user text turn.</item>
///   <item>Buffered collect: terminated digit buffer triggers the configured validator/transition.</item>
/// </list>
/// </summary>
public class RealtimeVoiceStrategyDtmfTests
{
    [Fact]
    public async Task Menu_digit_drives_transition_without_invoking_backend_user_text()
    {
        var workflow = BuildWorkflowWithMenu();
        var backend = new ControllableRealtimeBackend("rt-agent", "RT Agent");

        var (strategy, dtmfWriter, context) = await StartStrategyAsync(backend, workflow);

        try
        {
            // Press '1' → 'verify' stage per the menu mapping below.
            await dtmfWriter.WriteAsync(new DtmfTone('1', DateTimeOffset.UtcNow));

            var entered = await WaitForEventAsync<StrategyEvent.WorkflowStepEntered>(
                strategy.Events, e => e.StepId == "verify", TimeSpan.FromSeconds(2));

            Assert.Equal("verify", entered.StepId);
            // The LLM-aware path must NOT fire for a stage-resolved digit.
            Assert.Empty(backend.ReceivedUserText);
        }
        finally
        {
            await TeardownAsync(strategy, context);
        }
    }

    [Fact]
    public async Task Digit_without_scripted_dtmf_is_forwarded_as_user_text_turn()
    {
        var workflow = BuildWorkflowWithoutScripted();
        var backend = new ControllableRealtimeBackend("rt-agent", "RT Agent");

        var (strategy, dtmfWriter, context) = await StartStrategyAsync(backend, workflow);

        try
        {
            await dtmfWriter.WriteAsync(new DtmfTone('5', DateTimeOffset.UtcNow));

            // The pump should hand the digit to the backend.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (backend.ReceivedUserText.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.Single(backend.ReceivedUserText);
            Assert.Contains("5", backend.ReceivedUserText[0]);
            Assert.Contains("Caller pressed", backend.ReceivedUserText[0]);
        }
        finally
        {
            await TeardownAsync(strategy, context);
        }
    }

    [Fact]
    public async Task Collected_digits_terminated_by_pound_trigger_on_valid_next_stage()
    {
        var workflow = BuildWorkflowWithCollect();
        var backend = new ControllableRealtimeBackend("rt-agent", "RT Agent");

        var (strategy, dtmfWriter, context) = await StartStrategyAsync(backend, workflow);

        try
        {
            // maxNumberOfDigits=4 commits the buffer automatically after the 4th digit, so
            // we do NOT need to send '#' (sending it after the transition would land on
            // the next stage with no scripted DTMF and hit the LLM-aware fallback).
            await dtmfWriter.WriteAsync(new DtmfTone('1', DateTimeOffset.UtcNow));
            await dtmfWriter.WriteAsync(new DtmfTone('2', DateTimeOffset.UtcNow));
            await dtmfWriter.WriteAsync(new DtmfTone('3', DateTimeOffset.UtcNow));
            await dtmfWriter.WriteAsync(new DtmfTone('4', DateTimeOffset.UtcNow));

            var entered = await WaitForEventAsync<StrategyEvent.WorkflowStepEntered>(
                strategy.Events, e => e.StepId == "verified", TimeSpan.FromSeconds(2));

            Assert.Equal("verified", entered.StepId);
            // Collected digits are stored under the configured state key.
            Assert.Equal("1234", strategy.WorkflowState.Get<string>("pinDigits"));
            // No LLM-aware forwarding when collect path resolves cleanly.
            Assert.Empty(backend.ReceivedUserText);
        }
        finally
        {
            await TeardownAsync(strategy, context);
        }
    }

    // ---------- helpers ----------

    private static async Task<(RealtimeVoiceStrategy strategy, ChannelWriter<DtmfTone> dtmfWriter, StrategyStartContext ctx)>
        StartStrategyAsync(ControllableRealtimeBackend backend, RealtimeIvrWorkflowDefinition workflow)
    {
        var services = new ServiceCollection()
            .AddSingleton(TestTelemetry.Calling)
            .AddLogging()
            .BuildServiceProvider();

        var strategy = new RealtimeVoiceStrategy(
            backend,
            workflow,
            TestTelemetry.LoggerFactory,
            TestTelemetry.Calling);

        var audio = Channel.CreateUnbounded<AudioFrame>();
        var dtmf = Channel.CreateUnbounded<DtmfTone>();

        var ctx = new StrategyStartContext
        {
            CallId = "call-rt-dtmf",
            InboundAudio = audio.Reader,
            InboundDtmf = dtmf.Reader,
            Services = services,
            CallerMetadata = null,
        };

        await strategy.StartAsync(ctx);
        Assert.True(await backend.WaitForConnectAsync(TimeSpan.FromSeconds(2)));
        return (strategy, dtmf.Writer, ctx);
    }

    private static async Task TeardownAsync(RealtimeVoiceStrategy strategy, StrategyStartContext _)
    {
        await strategy.StopAsync();
        await strategy.DisposeAsync();
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

    private static RealtimeIvrWorkflowDefinition BuildWorkflowWithMenu() => new()
    {
        Name = "rt-dtmf-menu",
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
                    Instructions = ["Greet the caller."],
                    Transitions = [new StateTransition { NextStep = "verify", Condition = "dtmf" }]
                },
                StepScriptedConfiguration = new StepScriptedConfiguration
                {
                    Dtmf = new StepDtmfConfiguration
                    {
                        MenuOptions = new Dictionary<char, DtmfMenuOption>
                        {
                            ['1'] = new() { Digit = '1', Label = "verify", NextStepId = "verify" },
                        }
                    }
                }
            },
            new RealtimeIvrWorkflowStep
            {
                Id = "verify",
                ConversationState = new ConversationState
                {
                    Id = "verify",
                    Description = "Verification",
                    Instructions = ["Verify"]
                }
            }
        ]
    };

    private static RealtimeIvrWorkflowDefinition BuildWorkflowWithoutScripted() => new()
    {
        Name = "rt-dtmf-llm",
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
                    Instructions = ["Greet the caller."]
                }
                // No StepScriptedConfiguration → LLM-aware fallback path.
            }
        ]
    };

    private static RealtimeIvrWorkflowDefinition BuildWorkflowWithCollect() => new()
    {
        Name = "rt-dtmf-collect",
        BasePrompt = new RealtimePrompt(),
        Steps =
        [
            new RealtimeIvrWorkflowStep
            {
                Id = "welcome",
                ConversationState = new ConversationState
                {
                    Id = "welcome",
                    Description = "Enter your PIN",
                    Instructions = ["Ask the caller for their four-digit PIN."],
                    Transitions = [new StateTransition { NextStep = "verified", Condition = "pin-ok" }]
                },
                StepScriptedConfiguration = new StepScriptedConfiguration
                {
                    Dtmf = new StepDtmfConfiguration(
                        terminationDigit: '#',
                        interDigitTimeoutMs: 5000,
                        minNumberOfDigits: 4,
                        maxNumberOfDigits: 4)
                    {
                        CollectedStateKey = "pinDigits",
                        OnValidNextStepId = "verified",
                    }
                }
            },
            new RealtimeIvrWorkflowStep
            {
                Id = "verified",
                ConversationState = new ConversationState
                {
                    Id = "verified",
                    Description = "Verified",
                    Instructions = ["You're verified."]
                }
            }
        ]
    };
}
