using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.LiveVoice.Media.Audio;
using Agents.AI.Extensions.LiveVoice.Media.Messaging;
using Agents.AI.Extensions.LiveVoice.Media.Signaling;
using Agents.AI.RealtimeVoice.Azure.Models;
using Agents.AI.RealtimeVoice.Azure.VoiceAgent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Transports;

/// <summary>
/// Transport for Tier 4 degradation: pure DTMF menu navigation with no AI dependency.
/// Drives the IVR workflow directly by mapping DTMF tone signals to workflow step
/// transitions using <see cref="RealtimeIvrWorkflowStep.StepDtmfConfiguration"/>.
/// <para>
/// Does not implement <see cref="IAudioConsumer"/> — voice input is ignored.
/// Only processes <see cref="SessionSignal"/> with <see cref="SessionSignalKind.Dtmf"/>.
/// Audio prompts are synthesized via <see cref="ISpeechSynthesizer"/> or played from
/// pre-recorded assets.
/// </para>
/// </summary>
public sealed class DtmfIvrTransport : IChannelTransport, ISignalConsumer, IAudioProducer, IMessageProducer
{
    private readonly RealtimeIvrWorkflowDefinition _workflow;
    private readonly IvrWorkflowState _workflowState;
    private readonly ISpeechSynthesizer _synthesizer;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly PresenceDetectorService? _presenceDetector;

    private Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> _audioHandler = (_, _, _) => Task.CompletedTask;
    private Func<string, MessageUpdate, CancellationToken, Task> _messageHandler = (_, _, _) => Task.CompletedTask;
    private Func<string, Task>? _disconnectedHandler;

    private string? _currentStepId;
    private string _digitBuffer = string.Empty;
    private bool _connected;

    public DtmfIvrTransport(
        RealtimeIvrWorkflowDefinition workflow,
        IvrWorkflowState workflowState,
        ISpeechSynthesizer synthesizer,
        PresenceDetectorService? presenceDetector = null,
        ILoggerFactory? loggerFactory = null)
    {
        _workflow = workflow;
        _workflowState = workflowState;
        _synthesizer = synthesizer;
        _presenceDetector = presenceDetector;
        _logger = loggerFactory?.CreateLogger<DtmfIvrTransport>()
                  ?? NullLogger<DtmfIvrTransport>.Instance;

        _currentStepId = workflow.InitialStepId;

        var transportId = $"dtmf-{Guid.NewGuid():N}";
        Metadata = new ParticipantTransportMetadata
        {
            ContactId = transportId,
            ChannelType = CommunicationChannelType.VoiceAIAgent,
            RawIdentifier = transportId,
            DisplayName = "DTMF IVR",
            Role = ChannelRole.PrimaryVoice | ChannelRole.InteractiveMessaging,
            SupportsAudio = true,  // produces audio via TTS
            SupportsMessaging = true
        };
    }

    public string ChannelId => Metadata.ContactId;
    public ParticipantTransportMetadata Metadata { get; }
    public bool IsConnected => _connected;

    /// <summary>
    /// Gets the current workflow state. Exposed for mid-call failover state capture.
    /// </summary>
    public IvrWorkflowState WorkflowState => _workflowState;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connected)
        {
            return;
        }

        _connected = true;

        // Play the initial step prompt
        await PlayCurrentStepPromptAsync(cancellationToken).ConfigureAwait(false);
    }

    public void SetOnAudioReceivedCallback(Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> handler) => _audioHandler = handler;
    public void SetOnMessageReceivedCallback(Func<string, MessageUpdate, CancellationToken, Task> handler) => _messageHandler = handler;
    public void SetOnDisconnected(Func<string, Task> handler) => _disconnectedHandler = handler;

    public async Task SendSignalAsync(SessionSignal signal, CancellationToken cancellationToken = default)
    {
        if (signal.Kind is not SessionSignalKind.Dtmf || signal.Value is null)
        {
            return;
        }

        _presenceDetector?.OnDtmfReceived(signal.Value);

        var digit = signal.Value[0];
        _logger.LogDebug("DTMF tone received: {Digit} at step {StepId}", digit, _currentStepId);

        if (_currentStepId is null)
        {
            return;
        }

        var step = _workflow.GetStep(_currentStepId);
        if (step is null)
        {
            return;
        }

        if (step.StepDtmfConfiguration?.MenuOptions is not null)
        {
            // Menu mode: single digit selects an option
            await ProcessMenuSelectionAsync(step, digit, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Digit collection mode: '#' terminates input
            await ProcessDigitCollectionAsync(step, digit, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessMenuSelectionAsync(RealtimeIvrWorkflowStep step, char digit, CancellationToken cancellationToken)
    {
        if (step.StepDtmfConfiguration?.MenuOptions is null || !step.StepDtmfConfiguration.MenuOptions.TryGetValue(digit, out var option))
        {
            _logger.LogDebug("Invalid DTMF selection '{Digit}' for step {StepId}", digit, _currentStepId);
            await SynthesizeAndSendAudioAsync("That is not a valid option. Please try again.", cancellationToken).ConfigureAwait(false);

            return;
        }

        var selectedOption = option.NextStepId ?? option.Label;

        _logger.LogInformation("DTMF selection: '{Digit}' → '{Option}' at step {StepId}", digit, selectedOption, _currentStepId);

        // Store selection in workflow state
        _workflowState.Set($"{_currentStepId}_selection", option.Label);

        // Emit selection as a message for conversation context
        await EmitMessageAsync($"Selected: {selectedOption}", "user", cancellationToken).ConfigureAwait(false);

        // Check if the selection maps to a valid transition
        var transitions = step.ValidTransitions;
        var matchedTransition = transitions.FirstOrDefault(t =>
            string.Equals(t, selectedOption, StringComparison.OrdinalIgnoreCase));

        if (matchedTransition is not null)
        {
            _workflowState.MarkStepCompleted(_currentStepId!);
            _currentStepId = matchedTransition;
            _workflowState.CurrentStepName = _currentStepId;
            await PlayCurrentStepPromptAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (transitions.Count > 0)
        {
            // Auto-advance to next step if there's exactly one transition
            _workflowState.MarkStepCompleted(_currentStepId!);
            _currentStepId = transitions[0];
            _workflowState.CurrentStepName = _currentStepId;
            await PlayCurrentStepPromptAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessDigitCollectionAsync(RealtimeIvrWorkflowStep step, char digit, CancellationToken cancellationToken)
    {
        if (digit == '#')
        {
            // '#' terminates the digit sequence
            if (_digitBuffer.Length > 0)
            {
                _logger.LogInformation("DTMF digit collection complete: '{Digits}' at step {StepId}", _digitBuffer, _currentStepId);
                _workflowState.Set(_currentStepId!, _digitBuffer);
                await EmitMessageAsync($"Entered: {_digitBuffer}", "user", cancellationToken).ConfigureAwait(false);

                _digitBuffer = string.Empty;

                // Auto-advance to next step
                var transitions = step.ValidTransitions;
                if (transitions.Count > 0)
                {
                    _workflowState.MarkStepCompleted(_currentStepId!);
                    _currentStepId = transitions[0];
                    _workflowState.CurrentStepName = _currentStepId;
                    await PlayCurrentStepPromptAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        else if (digit == '*')
        {
            // '*' clears the buffer
            _digitBuffer = string.Empty;
            await SynthesizeAndSendAudioAsync("Cleared. Please enter your digits again.", cancellationToken).ConfigureAwait(false);
        }
        else if (char.IsDigit(digit))
        {
            _digitBuffer += digit;
        }
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

        // Build prompt text from step description + DTMF menu options
        var prompt = step.ConversationState.Description ?? step.ConversationState.Goal ?? string.Empty;

        if (step.StepDtmfConfiguration?.MenuOptions is { Count: > 0 } menu)
        {
            var menuText = string.Join(". ", menu.Select(kv => $"Press {kv.Key} for {kv.Value.Label}"));
            prompt = $"{prompt}. {menuText}.";
        }
        else if (!string.IsNullOrWhiteSpace(prompt))
        {
            prompt = $"{prompt}. Enter your response followed by the pound sign.";
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        await SynthesizeAndSendAudioAsync(prompt, cancellationToken).ConfigureAwait(false);
        await EmitMessageAsync(prompt, "assistant", cancellationToken).ConfigureAwait(false);
    }

    private async Task SynthesizeAndSendAudioAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var audioFrame in _synthesizer.SynthesizeAsync(text, SynthesizerInputFormat.SSML, cancellationToken).ConfigureAwait(false))
            {
                await _audioHandler(ChannelId, audioFrame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TTS synthesis failed for DTMF transport {ChannelId}", ChannelId);
        }
    }

    private async Task EmitMessageAsync(string text, string role, CancellationToken cancellationToken)
    {
        var msg = new MessageUpdate
        {
            Contents = [new TextContent(text)],
            Role = role,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _messageHandler(ChannelId, msg, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
        _connected = false;

        if (_disconnectedHandler is not null)
        {
            try { await _disconnectedHandler(ChannelId); } catch { }
        }
    }
}
