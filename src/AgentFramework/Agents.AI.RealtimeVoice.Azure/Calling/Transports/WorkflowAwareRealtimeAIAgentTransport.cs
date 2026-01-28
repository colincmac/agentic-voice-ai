using System.ComponentModel;
using System.Text.Json;
using System.Threading.Channels;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.Extensions.LiveVoice;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;
using Extensions.AI.Contents;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Components.Web;
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
    private readonly LiveConversationAgentSession _thread;
    private readonly AgentRunOptions? _runOptions;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();

    private Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> _audioHandler = (_, _, _) => Task.CompletedTask;
    private Func<string, MessageUpdate, CancellationToken, Task> _messageHandler = (_, _, _) => Task.CompletedTask;
    private Func<string, Task>? _disconnectedHandler = _ => Task.CompletedTask;

    private readonly Channel<DataContent> _inboundAudioChannel;
    private readonly Channel<AgentRunResponseUpdate> _agentUpdates;
    private Task? _backgroundLoop;
    private RealtimeIvrStepConfiguration? _currentStepConfig;
    private readonly SemaphoreSlim _configUpdateLock = new(1, 1);
    internal const string FunctionPrefix = "transition_to_";
    private static readonly JsonElement handoffSchema = AIFunctionFactory.Create(
    ([Description("The reason for the handoff")] string? reasonForHandoff) => { }).JsonSchema;
    private readonly IvrWorkflowState _stateCache;

    private readonly RealtimeIvrWorkflowDefinition _workflowDefinition;
    /// <summary>
    /// Raised when a workflow step transition occurs.
    /// </summary>
    public event Func<RealtimeIvrStepConfiguration, CancellationToken, Task>? OnStepTransition;

    public WorkflowAwareRealtimeAIAgentTransport(
        AuthorizingRealtimeAIAgent agent,
        LiveConversationAgentSession existingThread,
        RealtimeIvrWorkflowDefinition workflowDefinition,
        AgentRunOptions? runOptions = null,
        ILoggerFactory? loggerFactory = null)
    {
        _agent = agent;
        _thread = existingThread;
        _runOptions = runOptions;
        _stateCache = new IvrWorkflowState()
        {
            Status = IvrWorkflowStatus.NotStarted,
            CurrentStepName = workflowDefinition.GetStep(workflowDefinition.InitialStepId)?.Id,
        };

        _logger = loggerFactory?.CreateLogger<WorkflowAwareRealtimeAIAgentTransport>()
                  ?? NullLogger<WorkflowAwareRealtimeAIAgentTransport>.Instance;
        _workflowDefinition = workflowDefinition;
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
        _agentUpdates = Channel.CreateUnbounded<AgentRunResponseUpdate>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });
    }

    public string ChannelId => Metadata.ContactId;
    public ParticipantTransportMetadata Metadata { get; }
    public bool IsConnected => _backgroundLoop is not null;


    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_backgroundLoop is not null)
        {
            return;
        }
        // Initialize the coordinator and get the initial configuration

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _backgroundLoop = Task.WhenAll(
            RunAgentStreamAsync(linkedCts.Token),
            RunSendLoopAsync(linkedCts.Token),
            ProcessAgentUpdatesAsync(linkedCts.Token)
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

    private async Task ProcessAgentUpdatesAsync(CancellationToken cancellationToken) 
    {
        DateTimeOffset agentTurnStart = DateTimeOffset.UtcNow;
        DateTimeOffset? agentTurnEnd = null;
        DateTimeOffset userTurnStart = DateTimeOffset.UtcNow;
        DateTimeOffset? userTurnEnd = null;
        var pendingUpdates = new List<AgentRunResponseUpdate>();
        try
        {
            await foreach (var update in _agentUpdates.Reader.ReadAllAsync(cancellationToken))
            {
                List<AIContent> nonAudio = [];
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case DataContent dc when !dc.Data.IsEmpty:
                            await _audioHandler(ChannelId, dc.Data, cancellationToken).ConfigureAwait(false);
                            break;

                        case RealtimeVadContent vc:
                            // Mark turn start on user speech start
                            nonAudio.Add(content);
                            if (vc.VadEvent == VadEventType.InputSpeechStarted)
                            {
                                userTurnStart = vc.TimeStamp;
                            }
                            else if (vc.VadEvent == VadEventType.InputSpeechEnded)
                            {
                                userTurnEnd = vc.TimeStamp;
                            }
                            else if (vc.VadEvent == VadEventType.OutputSpeechStarted)
                            {
                                agentTurnStart = vc.TimeStamp;
                            }
                            else if (vc.VadEvent == VadEventType.OutputSpeechEnded)
                            {
                                agentTurnEnd = vc.TimeStamp;
                            }
     
                            break;

                        case TextContent tc when !string.IsNullOrWhiteSpace(tc.Text):
                            // Text content is sent when transcription is complete for an utterance for both the user and agent
                            nonAudio.Add(content);

                            if (update.Role == ChatRole.User)
                            {
                                await ProcessUtteranceTranscriptAsync(ChatRole.User, userTurnStart, userTurnEnd, tc, cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                await ProcessUtteranceTranscriptAsync(ChatRole.Assistant, agentTurnStart, agentTurnEnd, tc, cancellationToken).ConfigureAwait(false);
                            }
                            break;

                        default:
                            nonAudio.Add(content);
                            break;
                    }
                }

                // Handle non-audio content
                if (nonAudio is { Count: > 0 })
                {
                    update.Contents = nonAudio;
                    var msg = MessageUpdateExtensions.FromAgentRunResponseUpdate(update);
                    await _messageHandler(ChannelId, msg, cancellationToken).ConfigureAwait(false);
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
    }

    private Task ProcessUtteranceTranscriptAsync(ChatRole role, DateTimeOffset turnStartTime, DateTimeOffset? turnEndTime, TextContent transcript, CancellationToken cancellationToken)
    {

        try
        {
            _stateCache.AddUtterance(new RealtimeConversationUtterance(new ChatMessage(role, [transcript]))
            {
                UtteranceStartTime = turnStartTime,
                UtteranceEndTime = turnEndTime
            });
            _stateCache.TotalTurns++;

            // Queue orchestrator evaluation (non-blocking) after user utterances
            // The background processor will debounce and evaluate
            //if (role == ChatRole.User && _thread is not null)
            //{
            //    QueueOrchestratorEvaluation();
            //}
            if (role == ChatRole.User)
            {

            }
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing completed turn for workflow");
            return Task.CompletedTask;
        }
    }

    private async Task RunAgentStreamAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in _agent.RunStreamingAsync(_thread, _runOptions, cancellationToken).ConfigureAwait(false))
            {
               await _agentUpdates.Writer.WriteAsync(update, cancellationToken);
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
    

    //private async Task ProcessCompletedTurnAsync(
    //    RealtimeVoiceAgentTurn turn,
    //    CancellationToken cancellationToken)
    //{
    //    try
    //    {
    //        // Let the coordinator analyze the turn
    //        var newConfig = await _coordinator.ProcessTurnAsync(turn, cancellationToken);

    //        // If a step transition occurred, update the agent's configuration
    //        if (newConfig is not null)
    //        {
    //            await ApplyStepConfigurationAsync(newConfig, cancellationToken);
    //        }
    //    }
    //    catch (Exception ex)
    //    {
    //        _logger.LogError(ex, "Error processing completed turn for workflow");
    //    }
    //}

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
