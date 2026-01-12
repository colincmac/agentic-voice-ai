using System.Threading.Channels;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.Extensions.LiveVoice;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;
using Extensions.AI.Contents;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Transports;

/// <summary>
/// Realtime AI agent transport with IVR workflow integration.
/// This transport runs the realtime agent stream while coordinating with an IVR workflow
/// to dynamically update the agent's configuration as the workflow progresses.
/// </summary>
public sealed class WorkflowAwareRealtimeAIAgentTransport : IChannelTransport
{
    private readonly AuthorizingRealtimeAIAgent _agent;
    private readonly ConversationSessionThread _thread;
    private readonly RealtimeIvrWorkflowCoordinator _coordinator;
    private readonly AgentRunOptions? _runOptions;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();

    private Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> _audioHandler = (_, _, _) => Task.CompletedTask;
    private Func<string, MessageUpdate, CancellationToken, Task> _messageHandler = (_, _, _) => Task.CompletedTask;
    private Func<string, Task>? _disconnectedHandler = _ => Task.CompletedTask;

    private readonly Channel<DataContent> _inboundAudioChannel;
    private Task? _backgroundLoop;
    private RealtimeIvrStepConfiguration? _currentStepConfig;
    private readonly SemaphoreSlim _configUpdateLock = new(1, 1);

    /// <summary>
    /// Raised when a workflow step transition occurs.
    /// </summary>
    public event Func<RealtimeIvrStepConfiguration, CancellationToken, Task>? OnStepTransition;

    public WorkflowAwareRealtimeAIAgentTransport(
        AuthorizingRealtimeAIAgent agent,
        ConversationSessionThread existingThread,
        RealtimeIvrWorkflowCoordinator coordinator,
        AgentRunOptions? runOptions = null,
        ILoggerFactory? loggerFactory = null)
    {
        _agent = agent;
        _thread = existingThread;
        _coordinator = coordinator;
        _runOptions = runOptions;
        _logger = loggerFactory?.CreateLogger<WorkflowAwareRealtimeAIAgentTransport>()
                  ?? NullLogger<WorkflowAwareRealtimeAIAgentTransport>.Instance;

        Metadata = new ParticipantTransportMetadata
        {
            ContactId = agent.Id,
            ChannelType = CommunicationChannelType.VoiceAIAgent,
            RawIdentifier = existingThread.ActiveSessionId ?? agent.Id,
            DisplayName = agent.DisplayName,
            SupportsAudio = true,
            SupportsMessaging = true
        };

        _inboundAudioChannel = Channel.CreateBounded<DataContent>(new BoundedChannelOptions(500)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = true
        });

        // Wire up coordinator events
        _coordinator.OnStepChanged += HandleStepChangedAsync;
    }

    public string ChannelId => Metadata.ContactId;
    public ParticipantTransportMetadata Metadata { get; }
    public bool IsConnected => _backgroundLoop is not null;

    /// <summary>
    /// Gets the current workflow state.
    /// </summary>
    public IvrWorkflowState WorkflowState => _coordinator.WorkflowState;

    /// <summary>
    /// Gets the current step ID.
    /// </summary>
    public string? CurrentStepId => _coordinator.CurrentStepId;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_backgroundLoop is not null)
        {
            return;
        }

        // Initialize the coordinator and get the initial configuration
        _currentStepConfig = await _coordinator.InitializeAsync(cancellationToken);

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _backgroundLoop = Task.WhenAll(
            RunAgentStreamAsync(linkedCts.Token),
            RunSendLoopAsync(linkedCts.Token)
        );
    }

    public void OnAudioReceived(Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> handler) => _audioHandler = handler;
    public void OnMessageReceived(Func<string, MessageUpdate, CancellationToken, Task> handler) => _messageHandler = handler;
    public void OnDisconnected(Func<string, Task> handler) => _disconnectedHandler = handler;

    public async Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        var dataContent = new DataContent(audioData.ToArray(), "audio/pcm");
        await _inboundAudioChannel.Writer.WriteAsync(dataContent, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default)
    {
        var chat = MessageUpdateExtensions.ToChatMessage(message);
        await _agent.SendMessagesToRunAsync([chat], _thread, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunSendLoopAsync(CancellationToken ct)
    {
        await foreach (var dataContent in _inboundAudioChannel.Reader.ReadAllAsync(ct))
        {
            await _agent.SendAudioToRunAsync(dataContent, _thread, ct).ConfigureAwait(false);
        }
    }

    private async Task RunAgentStreamAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset turnStartTime = DateTimeOffset.UtcNow;
        DateTimeOffset? turnEndTime = null;
        var pendingUpdates = new List<AgentRunResponseUpdate>();

        try
        {
            await foreach (var update in _agent.RunStreamingAsync(_thread, _runOptions, cancellationToken).ConfigureAwait(false))
            {
                ReadOnlyMemory<byte>? audioFrame = null;
                List<AIContent> nonAudio = [];
                var isTurnComplete = false;

                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case DataContent dc:
                            audioFrame = dc.Data;
                            break;
                        case RealtimeVadContent vc when vc.VadEvent == VadEventType.InputSpeechStarted:
                            // Mark turn start on user speech start
                            nonAudio.Add(content);
                            turnStartTime = vc.TimeStamp;
                            break;
                        case RealtimeResponseFinishedContent fc:
                            // Mark turn end on response finished
                            isTurnComplete = true;
                            nonAudio.Add(content);
                            turnEndTime = fc.FinishedAt;
                            break;
                        default:
                            nonAudio.Add(content);
                            break;
                    }
                }

                // Handle audio output
                if (audioFrame.HasValue)
                {
                    await _audioHandler(ChannelId, audioFrame.Value, cancellationToken).ConfigureAwait(false);
                }

                // Handle non-audio content
                if (nonAudio is { Count: > 0 })
                {
                    update.Contents = nonAudio;
                    var msg = MessageUpdateExtensions.FromAgentRunResponseUpdate(update);
                    await _messageHandler(ChannelId, msg, cancellationToken).ConfigureAwait(false);
                }

                // Track updates for turn aggregation
                pendingUpdates.Add(update);

                // When a turn completes, analyze it for workflow progression
                if (isTurnComplete)
                {
                    var chatResponse = pendingUpdates
                        .Select(u => AsChatResponseUpdate(u))
                        .ToChatResponse();

                    var turn = new RealtimeVoiceAgentTurn([.. chatResponse.Messages])
                    {
                        TurnStartTime = turnStartTime,
                        TurnEndTime = turnEndTime
                    };

                    await ProcessCompletedTurnAsync(turn, cancellationToken);
                    pendingUpdates.Clear();
                    isTurnComplete = false; // reset for next turn
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Workflow-aware realtime agent run error for {ChannelId}", ChannelId);
        }
        finally
        {
            if (_disconnectedHandler is not null)
            {
                try
                {
                    await _disconnectedHandler(ChannelId);
                }
                catch
                {
                    // Ignore errors during disconnect handling
                }
            }
        }
    }

    private async Task ProcessCompletedTurnAsync(
        RealtimeVoiceAgentTurn turn,
        CancellationToken cancellationToken)
    {
        try
        {
            // Let the coordinator analyze the turn
            var newConfig = await _coordinator.ProcessTurnAsync(turn, cancellationToken);

            // If a step transition occurred, update the agent's configuration
            if (newConfig is not null)
            {
                await ApplyStepConfigurationAsync(newConfig, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing completed turn for workflow");
        }
    }

    private async Task ApplyStepConfigurationAsync(
        RealtimeIvrStepConfiguration config,
        CancellationToken cancellationToken)
    {
        await _configUpdateLock.WaitAsync(cancellationToken);
        try
        {
            _currentStepConfig = config;

            _logger.LogInformation(
                "Applying step configuration for step {StepId} with {ToolCount} tools",
                config.StepId,
                config.AvailableTools.Count);

            // Update the realtime session with new instructions and tools
            var sessionOptions = new LiveConversationSessionOptions
            {
                Instructions = config.SystemPrompt,
                Tools = [.. config.AvailableTools]
            };

            await _agent.ConfigureSessionAsync(sessionOptions, _thread, cancellationToken).ConfigureAwait(false);

            if (OnStepTransition is not null)
            {
                await OnStepTransition(config, cancellationToken);
            }
        }
        finally
        {
            _configUpdateLock.Release();
        }
    }

    private async Task HandleStepChangedAsync(
        RealtimeIvrStepConfiguration config,
        CancellationToken cancellationToken)
    {
        await ApplyStepConfigurationAsync(config, cancellationToken);
    }

    private static ChatResponseUpdate AsChatResponseUpdate(AgentRunResponseUpdate responseUpdate)
    {
        return responseUpdate.RawRepresentation as ChatResponseUpdate ??
               new ChatResponseUpdate
               {
                   AdditionalProperties = responseUpdate.AdditionalProperties,
                   AuthorName = responseUpdate.AuthorName,
                   Contents = responseUpdate.Contents,
                   CreatedAt = responseUpdate.CreatedAt,
                   MessageId = responseUpdate.MessageId,
                   RawRepresentation = responseUpdate,
                   ResponseId = responseUpdate.ResponseId,
                   Role = responseUpdate.Role,
               };
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        if (_backgroundLoop is not null)
        {
            try
            {
                await _backgroundLoop.ConfigureAwait(false);
            }
            catch
            {
                // Ignore exceptions during disposal
            }
        }

        _thread.Dispose();
        _cts.Dispose();
        _configUpdateLock.Dispose();

        if (_disconnectedHandler is not null)
        {
            try
            {
                await _disconnectedHandler(ChannelId);
            }
            catch
            {
                // Ignore
            }
        }
    }
}
