using System.Threading.Channels;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.LiveVoice.Media.Audio;
using Agents.AI.Extensions.LiveVoice.Media.Messaging;
using Agents.AI.Extensions.LiveVoice.Media.Transcription;
using Agents.AI.RealtimeVoice.Azure.Models;
using Agents.AI.RealtimeVoice.Azure.VoiceAgent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Transports;

/// <summary>
/// Transport for Tier 3 degradation: STT → intent classification → deterministic response → TTS.
/// No generative AI is involved — utterances are classified into intents using <see cref="IIntentClassifier"/>
/// and mapped directly to IVR workflow transitions.
/// </summary>
public sealed class NluIntentTransport : IChannelTransport, IAudioConsumer, IAudioProducer, IMessageConsumer, IMessageProducer, ITranscriptProducer
{
    private readonly IIntentClassifier _classifier;
    private readonly RealtimeIvrWorkflowDefinition _workflow;
    private readonly IvrWorkflowState _workflowState;
    private readonly ISpeechRecognizer _recognizer;
    private readonly ISpeechSynthesizer _synthesizer;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly PresenceDetectorService? _presenceDetector;

    private Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> _audioHandler = (_, _, _) => Task.CompletedTask;
    private Func<string, MessageUpdate, CancellationToken, Task> _messageHandler = (_, _, _) => Task.CompletedTask;
    private Func<string, TranscriptSegment, CancellationToken, Task> _transcriptHandler = (_, _, _) => Task.CompletedTask;
    private Func<string, Task>? _disconnectedHandler;

    private readonly Channel<ReadOnlyMemory<byte>> _inboundAudioChannel;
    private Task? _backgroundLoop;
    private string? _currentStepId;

    public NluIntentTransport(
        IIntentClassifier classifier,
        RealtimeIvrWorkflowDefinition workflow,
        IvrWorkflowState workflowState,
        ISpeechRecognizer recognizer,
        ISpeechSynthesizer synthesizer,
        PresenceDetectorService? presenceDetector = null,
        ILoggerFactory? loggerFactory = null)
    {
        _classifier = classifier;
        _workflow = workflow;
        _workflowState = workflowState;
        _recognizer = recognizer;
        _synthesizer = synthesizer;
        _presenceDetector = presenceDetector;
        _logger = loggerFactory?.CreateLogger<NluIntentTransport>()
                  ?? NullLogger<NluIntentTransport>.Instance;

        _currentStepId = workflow.InitialStepId;

        var transportId = $"nlu-{Guid.NewGuid():N}";
        Metadata = new ParticipantTransportMetadata
        {
            ContactId = transportId,
            ChannelType = CommunicationChannelType.VoiceAIAgent,
            RawIdentifier = transportId,
            DisplayName = "NLU Intent Agent",
            Role = ChannelRole.PrimaryVoice | ChannelRole.InteractiveMessaging,
            SupportsAudio = true,
            SupportsMessaging = true
        };

        _inboundAudioChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(500)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = true
        });
    }

    public string ChannelId => Metadata.ContactId;
    public ParticipantTransportMetadata Metadata { get; }
    public bool IsConnected => _backgroundLoop is not null;

    /// <summary>
    /// Gets the current workflow state. Exposed for mid-call failover state capture.
    /// </summary>
    public IvrWorkflowState WorkflowState => _workflowState;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_backgroundLoop is not null)
        {
            return Task.CompletedTask;
        }

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _backgroundLoop = Task.WhenAll(
            RunSttPipelineAsync(linkedCts.Token),
            RunIntentClassificationLoopAsync(linkedCts.Token)
        );

        return Task.CompletedTask;
    }

    public void SetOnAudioReceivedCallback(Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> handler) => _audioHandler = handler;
    public void SetOnMessageReceivedCallback(Func<string, MessageUpdate, CancellationToken, Task> handler) => _messageHandler = handler;
    public void SetOnTranscriptReceivedCallback(Func<string, TranscriptSegment, CancellationToken, Task> handler) => _transcriptHandler = handler;
    public void SetOnDisconnected(Func<string, Task> handler) => _disconnectedHandler = handler;

    public async Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        await _inboundAudioChannel.Writer.WriteAsync(audioData, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default)
    {
        var textContent = message.Contents.OfType<TextContent>().FirstOrDefault();
        if (textContent?.Text is not null)
        {
            _presenceDetector?.OnChatMessageReceived(textContent.Text);
            await ProcessUtteranceAsync(textContent.Text, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RunSttPipelineAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var audioData in _inboundAudioChannel.Reader.ReadAllAsync(cancellationToken))
            {
                await _recognizer.WriteAudioAsync(audioData, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await _recognizer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RunIntentClassificationLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Play the initial step prompt
            await PlayCurrentStepPromptAsync(cancellationToken).ConfigureAwait(false);

            await foreach (var segment in _recognizer.GetTranscriptsAsync(cancellationToken).ConfigureAwait(false))
            {
                await _transcriptHandler(ChannelId, segment, cancellationToken).ConfigureAwait(false);

                if (!segment.IsFinal || string.IsNullOrWhiteSpace(segment.Text))
                {
                    continue;
                }

                _presenceDetector?.OnChatMessageReceived(segment.Text);
                await ProcessUtteranceAsync(segment.Text, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NLU intent classification loop failed for {ChannelId}", ChannelId);
        }
        finally
        {
            if (_disconnectedHandler is not null)
            {
                try { await _disconnectedHandler(ChannelId); } catch { }
            }
        }
    }

    private async Task ProcessUtteranceAsync(string utterance, CancellationToken cancellationToken)
    {
        if (_currentStepId is null)
        {
            return;
        }

        var step = _workflow.GetStep(_currentStepId);
        if (step is null)
        {
            return;
        }

        // Get valid intents from the step's transitions
        var validIntents = step.ValidTransitions;
        if (validIntents.Count == 0)
        {
            _logger.LogDebug("Step {StepId} has no transitions; treating utterance as free-form input", _currentStepId);
            _workflowState.Set(_currentStepId, utterance);

            return;
        }

        var result = await _classifier.ClassifyAsync(utterance, validIntents, cancellationToken).ConfigureAwait(false);

        if (result.IsNone)
        {
            _logger.LogDebug("No intent matched for utterance in step {StepId}", _currentStepId);
            var reprompt = "I didn't understand that. Please try again.";
            await SynthesizeAndSendAudioAsync(reprompt, cancellationToken).ConfigureAwait(false);
            await EmitMessageAsync(reprompt, cancellationToken).ConfigureAwait(false);

            return;
        }

        _logger.LogInformation(
            "Classified intent {Intent} (confidence: {Confidence:F2}) for step {StepId}",
            result.IntentName,
            result.Confidence,
            _currentStepId);

        // Store extracted entities in workflow state
        if (result.Entities is not null)
        {
            foreach (var (key, value) in result.Entities)
            {
                _workflowState.Set(key, value);
            }
        }

        // Transition to the matched step
        _workflowState.MarkStepCompleted(_currentStepId);
        _currentStepId = result.IntentName;
        _workflowState.CurrentStepName = _currentStepId;

        await PlayCurrentStepPromptAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PlayCurrentStepPromptAsync(CancellationToken cancellationToken)
    {
        if (_currentStepId is null)
        {
            return;
        }

        var step = _workflow.GetStep(_currentStepId);
        if (step is null)
        {
            return;
        }

        var prompt = step.ConversationState.Description ?? step.ConversationState.Goal ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        await SynthesizeAndSendAudioAsync(prompt, cancellationToken).ConfigureAwait(false);
        await EmitMessageAsync(prompt, cancellationToken).ConfigureAwait(false);
    }

    private async Task SynthesizeAndSendAudioAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var audioFrame in _synthesizer.SynthesizeAsync(text, cancellationToken).ConfigureAwait(false))
            {
                await _audioHandler(ChannelId, audioFrame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TTS synthesis failed for {ChannelId}", ChannelId);
        }
    }

    private async Task EmitMessageAsync(string text, CancellationToken cancellationToken)
    {
        var msg = new MessageUpdate
        {
            Contents = [new TextContent(text)],
            Role = "assistant",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _messageHandler(ChannelId, msg, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        if (_backgroundLoop is not null)
        {
            try { await _backgroundLoop.ConfigureAwait(false); } catch { }
        }

        _cts.Dispose();
        await _recognizer.DisposeAsync().ConfigureAwait(false);

        if (_disconnectedHandler is not null)
        {
            try { await _disconnectedHandler(ChannelId); } catch { }
        }
    }
}
