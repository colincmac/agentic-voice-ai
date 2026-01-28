using System.Runtime.CompilerServices;
using System.Text.Json;
using Extensions.AI.Contents;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Shared.Diagnostics;
using System;

namespace Agents.AI.RealtimeVoice;

public partial class RealtimeAIAgent : AIAgent, IRealtimeAIAgent
{
    private readonly RealtimeAgentOptions _agentOptions;


    public RealtimeAIAgent(
        ILiveConversationClient client,
        RealtimeAgentOptions? agentOptions = null,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? services = null) : base()
    {
        Client = client.WithDefaultAgentMiddleware(_agentOptions, services);
        Logger = (loggerFactory
                      ?? client.GetService<ILoggerFactory>()
                      ?? NullLoggerFactory.Instance).CreateLogger(GetType());
        _agentMeta = new AIAgentMetadata(client.Metadata.ProviderName);
        _agentOptions = agentOptions ?? new RealtimeAgentOptions();
    }


    // Configuration
    protected ILiveConversationClient Client { get; }
    protected ILogger Logger { get; }
    private readonly AIAgentMetadata _agentMeta;


    #region Public Surface

    public virtual Task SendAudioToRunAsync(
        DataContent audio,
        AgentThread thread,
        CancellationToken cancellationToken = default)
    {
        var liveThread = EnsureConversationSession(thread);
        return liveThread.Session.SendAudioAsync(audio.Data, cancellationToken);
    }

    public virtual Task SendMessagesToRunAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread thread,
        CancellationToken cancellationToken = default)
    {
        var liveThread = EnsureConversationSession(thread);
        return liveThread.Session.SendMessagesAsync(messages, cancellationToken);
    }

    public virtual async Task<LiveConversationAgentSession> GetNewSessionAsync(CancellationToken cancellationToken = default)
    {
        var session = await Client.GetSessionAsync(
            _agentOptions.SessionOptions,
            cancellationToken).ConfigureAwait(false);

        var thread = new LiveConversationAgentSession(session);
        return thread;
    }

    #endregion

    #region AIAgent overrides
    public override string Id => _agentOptions.Id ?? Guid.NewGuid().ToString();
    public override string Name => _agentOptions.Name ?? "RealtimeAIAgent";
    public override string? Description => _agentOptions.Description;

    public string? Instructions => this._agentOptions?.Instructions;

    public virtual Task<AgentRunResponse?> CancelRunAsync(string id, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<AgentRunResponse?>(null);
    }

    public virtual Task<AgentRunResponse?> DeleteRunAsync(string id, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<AgentRunResponse?>(null);
    }
    /// <summary>
    /// This method creates a realtime session and streams updates until completion, cancellation, or the TerminationPredicate is met.
    /// If the underlying session returns responses indefinitely, make sure to provide a TerminationPredicate in the AgentRunOptions.
    /// </summary>
    /// <param name="messages"></param>
    /// <param name="thread"></param>
    /// <param name="options"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override Task<AgentRunResponse> RunAsync(IEnumerable<ChatMessage> messages,
                                                    AgentThread? thread = null,
                                                    AgentRunOptions? options = null,
                                                    CancellationToken cancellationToken = default)
        => RunStreamingAsync(messages, thread, options, cancellationToken)
           .ToAgentRunResponseAsync(cancellationToken);

    /// <summary>
    /// Creates or reuses a realtime session and streams updates until:
    /// 1. The caller cancels via cancellationToken.
    /// 2. LiveVoiceAgentRunOptions.TerminationPredicate returns true for an update.
    /// 3. The agent is disposed of
    /// </summary>
    /// <param name="messages"></param>
    /// <param name="thread"></param>
    /// <param name="options"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var runOptions = options as RealtimeAgentRunOptions ?? new();
        thread ??= await GetNewSessionAsync(cancellationToken);
        var typedThread = EnsureConversationSession(thread);


        var (responseOptions, sessionOptions, sessionMessages) =
            await ConfigureThreadAndSessionAsync(typedThread, runOptions, messages, cancellationToken);

        var activeSession = typedThread.Session;
        List<AgentRunResponseUpdate> finishedResponses = [];
        IAsyncEnumerator<ChatResponseUpdate> responseUpdatesEnumerator;
        LiveConversationResponseOptions? nextTurnResponseOptions = null; //await ApplyAIContextToNextResponseAsync(typedThread, sessionMessages, responseOptions, cancellationToken);

        try
        {
            //if (runOptions.InitiateConversation)
            //{
            //    await activeSession.StartResponseAsync(nextTurnResponseOptions, cancellationToken).ConfigureAwait(false);
            //}

            responseUpdatesEnumerator = activeSession.GetStreamingResponseAsync(responseOptions, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception ex)
        {
            await NotifyAIContextProviderOfFailureAsync(
                typedThread, ex, sessionMessages, cancellationToken).ConfigureAwait(false);
            throw;
        }

        var hasUpdates = await responseUpdatesEnumerator.MoveNextAsync().ConfigureAwait(false);

        await activeSession.StartResponseAsync(responseOptions, cancellationToken).ConfigureAwait(false);

        while (hasUpdates)
        {
            var delta = responseUpdatesEnumerator.Current;

            if(delta is null)
            {
                hasUpdates = await responseUpdatesEnumerator.MoveNextAsync().ConfigureAwait(false);
                continue;
            }

            // There seems to be an Issue with ResponseToken types. Maybe fixed by https://github.com/microsoft/agent-framework/commit/a89c15d6e64f5d7719aa5d353e95143a21ec382b#diff-16ead8c6c074d7276aa5acffbfad81539fa9449d7de193f82b9011803b3d6a3d
            // Once fixed, AgentRunResponseUpdate can be directly constructed from delta, with additional Agent data added.
            delta.ContinuationToken = ResponseContinuationToken.FromBytes(new byte[] { 0x01 }); // Dummy token to indicate continuation
            var wrapped = new AgentRunResponseUpdate()
            {
                AgentId = Id,
                AuthorName = delta.AuthorName ?? Name,
                RawRepresentation = delta,
                
                AdditionalProperties = delta.AdditionalProperties,
                Contents = delta.Contents,
                CreatedAt = delta.CreatedAt,
                MessageId = delta.MessageId,
                ResponseId = delta.ResponseId,
                Role = delta.Role,
                ContinuationToken = delta.ContinuationToken,
            };

            yield return wrapped;

            finishedResponses.Add(wrapped);

            foreach (var content in delta.Contents)
            {
                //if (content is RealtimeVadContent vc && vc.VadEvent == VadEventType.InputSpeechEnded)
                //{
                //    await activeSession.StartResponseAsync(nextTurnResponseOptions, cancellationToken).ConfigureAwait(false);
                //}
                
                if (content is RealtimeResponseFinishedContent)
                {
                    nextTurnResponseOptions = await HandleAgentTurnEndedAsync(typedThread, finishedResponses, nextTurnResponseOptions, cancellationToken).ConfigureAwait(false);
                    finishedResponses.Clear();
                }
            }

            if (runOptions?.TerminationPredicate?.Invoke(wrapped) ?? false)
            {
                break;
            }


            hasUpdates = await responseUpdatesEnumerator.MoveNextAsync().ConfigureAwait(false);
        }

    }


    public override object? GetService(Type serviceType, object? serviceKey = null)
        => base.GetService(serviceType, serviceKey) ??
        (serviceType == typeof(AIAgentMetadata) ? _agentMeta :
        serviceType == typeof(RealtimeAgentOptions) ? _agentOptions :
        serviceType.IsInstanceOfType(this) ? this :
        serviceType.IsInstanceOfType(typeof(IRealtimeAIAgent)) ? this :
        serviceType == typeof(ILiveConversationClient) ? Client :
        Client.GetService(serviceType, serviceKey));

    public override LiveConversationAgentSession GetNewThread() => new(Client.GetSession(_agentOptions.SessionOptions))
    {
        MessageTranscriptStore = _agentOptions.ChatMessageStoreFactory?.Invoke(
            new() { SerializedState = default, JsonSerializerOptions = null }) ?? new InMemoryChatMessageStore(),
        AIContextProvider = _agentOptions.AIContextProviderFactory?.Invoke(
            new() { SerializedState = default, JsonSerializerOptions = null }),
    };

    public override AgentThread DeserializeThread(
        JsonElement serializedThread,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var transcriptThread = new TranscriptTrackingAgentThread(serializedThread, jsonSerializerOptions);
        // Create new thread with deserialized state
        var thread = new LiveConversationAgentSession(
            Client.GetSession(_agentOptions.SessionOptions),
            serializedThread,
            jsonSerializerOptions
            );

        return thread;
    }
    #endregion

    #region Private Methods

    private static LiveConversationAgentSession EnsureConversationSession(AgentThread? thread)
    {
        if (thread is not LiveConversationAgentSession liveThread)
        {
            throw new InvalidOperationException(
                "The provided thread is not compatible with this agent. " +
                "Use GetNewThread() to create a compatible thread.");
        }
        return liveThread;
    }

    private async Task<LiveConversationResponseOptions?> HandleAgentTurnEndedAsync(
        LiveConversationAgentSession thread,
        List<AgentRunResponseUpdate> finishedResponses,
        LiveConversationResponseOptions? currentOptions,
        CancellationToken cancellationToken)
    {
        if (finishedResponses is { Count: 0 }) return null;
        var agentResponse = ToAgentRunResponse(finishedResponses, Id);

        await thread.UpdateTranscriptMessagesAsync(agentResponse.Messages, cancellationToken).ConfigureAwait(false);
        await NotifyAIContextProviderOfSuccessAsync(
            thread,
            agentResponse.Messages.Where(m => m.Role == ChatRole.User),
            agentResponse.Messages,
            cancellationToken).ConfigureAwait(false);
        return await ApplyAIContextToNextResponseAsync(thread, agentResponse.Messages, currentOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Notify the <see cref="AIContextProvider"/> when an agent run succeeded, if there is an <see cref="AIContextProvider"/>.
    /// </summary>
    private static async ValueTask NotifyAIContextProviderOfSuccessAsync(TranscriptTrackingAgentThread thread, IEnumerable<ChatMessage> inputMessages, IEnumerable<ChatMessage> responseMessages, CancellationToken cancellationToken)
    {
        if (thread.AIContextProvider is not null)
        {
            await thread.AIContextProvider.InvokedAsync(new(inputMessages, null) { ResponseMessages = responseMessages },
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Notify the <see cref="AIContextProvider"/> of any failure during an agent run, if there is an <see cref="AIContextProvider"/>.
    /// </summary>
    private static async ValueTask NotifyAIContextProviderOfFailureAsync(TranscriptTrackingAgentThread thread, Exception ex, IEnumerable<ChatMessage> inputMessages, CancellationToken cancellationToken)
    {
        if (thread.AIContextProvider is not null)
        {
            await thread.AIContextProvider.InvokedAsync(new(inputMessages, null) { InvokeException = ex },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<AIContext?> GetAIContextForNextInvocation(TranscriptTrackingAgentThread thread, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        if (thread.AIContextProvider is not { } provider) return null;

        var invokingContext = new AIContextProvider.InvokingContext(messages);

        return await provider.InvokingAsync(invokingContext, cancellationToken);
    }

    private static ILiveConversationClient ApplyRunOptionsTransformationsToClient(RealtimeAgentRunOptions? options, ILiveConversationClient conversationClient)
    {
        if (options?.ConversationClientFactory is not null)
        {
            // If we have a custom chat client factory, we should use it to create a new chat client with the transformed tools.
            conversationClient = options.ConversationClientFactory(conversationClient);
            _ = Throw.IfNull(conversationClient);
        }

        return conversationClient;
    }


    private async Task<LiveConversationResponseOptions?> ApplyAIContextToNextResponseAsync(TranscriptTrackingAgentThread thread, IEnumerable<ChatMessage> messages, LiveConversationResponseOptions? currentOptions, CancellationToken cancellationToken)
    {
        var context = await GetAIContextForNextInvocation(thread, messages, cancellationToken)
            .ConfigureAwait(false);
        if (context is not { } aiContext) return currentOptions;

        var responseOptions = currentOptions?.Clone();
        if (aiContext?.Instructions is not null)
        {
            responseOptions ??= new LiveConversationResponseOptions();
            responseOptions.Instructions = string.IsNullOrWhiteSpace(responseOptions.Instructions)
                ? aiContext.Instructions
                : $"{responseOptions.Instructions}{Environment.NewLine}{aiContext.Instructions}";
        }

        if (aiContext?.Messages is { Count: > 0 })
        {
            await SendMessagesToRunAsync(aiContext.Messages, thread, cancellationToken);
        }

        if (aiContext?.Tools is { Count: > 0 })
        {
            responseOptions ??= new LiveConversationResponseOptions();
            foreach (var tool in aiContext.Tools.OfType<AIFunction>())
            {
                responseOptions.Tools ??= [];
                responseOptions.Tools.Add(tool);
            }
        }

        return responseOptions;

    }
    private async Task<(LiveConversationResponseOptions? responseOptions, LiveConversationSessionOptions? sessionOptions, IEnumerable<ChatMessage> messages)> ConfigureThreadAndSessionAsync(
               LiveConversationAgentSession thread,
               RealtimeAgentRunOptions? runOptions,
               IEnumerable<ChatMessage> initialMessages,
               CancellationToken cancellationToken)
    {
        var sessionOptions = GetSessionOptions(runOptions);
        var responseOptions = runOptions?.ResponseOptions?.Clone();

        var client = ApplyRunOptionsTransformationsToClient(runOptions, Client);

        await thread._sessionGate.WaitAsync(cancellationToken);
        try
        {
            // Load history and context
            List<ChatMessage> sessionHistory = [];

            if (thread.Session.State == RealtimeSessionState.None && thread.MessageTranscriptStore is not null)
            {
                // We have not started the Session yet, so we can load previous messages.
                var previousSessionMessages = await thread.MessageTranscriptStore.GetMessagesAsync(cancellationToken)
                        .ConfigureAwait(false);

                sessionHistory.AddRange(previousSessionMessages);
            }

            var aiContext = await GetAIContextForNextInvocation(thread, initialMessages, cancellationToken)
                .ConfigureAwait(false);

            if (aiContext?.Messages is { Count: > 0 })
            {
                sessionHistory.AddRange(aiContext.Messages);
            }

            if (aiContext?.Tools is { Count: > 0 })
            {
                sessionOptions ??= new();
                sessionOptions.Tools ??= [];

                foreach (var tool in aiContext.Tools.OfType<AIFunction>())
                {
                    sessionOptions.Tools.Add(tool);
                }
            }

            if (aiContext?.Instructions is not null)
            {
                sessionOptions ??= new();
                sessionOptions.Instructions = string.IsNullOrWhiteSpace(sessionOptions.Instructions)
                    ? aiContext.Instructions
                    : $"{sessionOptions.Instructions}{Environment.NewLine}{aiContext.Instructions}";
            }

            if (!string.IsNullOrWhiteSpace(this.Instructions))
            {
                sessionOptions ??= new();
                sessionOptions.Instructions = string.IsNullOrWhiteSpace(sessionOptions.Instructions) ? this.Instructions : $"{this.Instructions}{Environment.NewLine}{sessionOptions.Instructions}";
            }
            sessionHistory.AddRange(initialMessages);


            // Reuse existing session if still valid
            if (thread.Session is null or { State: RealtimeSessionState.Closed or RealtimeSessionState.Closing or RealtimeSessionState.Error })
            {
                thread.Session?.Dispose();

                thread.Session = await client.GetSessionAsync(
                    sessionOptions,
                    cancellationToken);

            }

            if (sessionOptions is not null)
            {
                await thread.Session.ConfigureSessionAsync(sessionOptions, cancellationToken);
            }

            await SendMessagesToRunAsync(sessionHistory, thread, cancellationToken);

            return (responseOptions, sessionOptions, sessionHistory);
        }
        finally { thread._sessionGate.Release(); }
    }

    private LiveConversationSessionOptions? GetSessionOptions(RealtimeAgentRunOptions? responseOptions = null)
    {
        var requestOptions = responseOptions?.SessionOptions?.Clone();

        if (this._agentOptions?.SessionOptions is null)
        {
            return requestOptions;
        }

        if (requestOptions is null)
        {
            // Clone defaults and ensure distinct tools.
            var cloned = _agentOptions.SessionOptions.Clone();
            if (cloned.Tools is { Count: > 0 })
            {
                cloned.Tools = EnsureDistinctTools(cloned.Tools, null);
            }
            return cloned;
        }

        requestOptions.Instructions ??= _agentOptions.SessionOptions.Instructions;
        requestOptions.TurnDetection ??= _agentOptions.SessionOptions.TurnDetection;
        requestOptions.Voice ??= _agentOptions.SessionOptions.Voice;
        requestOptions.InputAudioFormat ??= _agentOptions.SessionOptions.InputAudioFormat;
        requestOptions.OutputAudioFormat ??= _agentOptions.SessionOptions.OutputAudioFormat;
        requestOptions.InputTranscription ??= _agentOptions.SessionOptions.InputTranscription;
        requestOptions.ToolMode ??= _agentOptions.SessionOptions.ToolMode;
        requestOptions.Tools ??= _agentOptions.SessionOptions.Tools;
        requestOptions.Modalities ??= _agentOptions.SessionOptions.Modalities;
        requestOptions.MaxOutputTokens ??= _agentOptions.SessionOptions.MaxOutputTokens;

        if (requestOptions.AdditionalProperties is not null && this._agentOptions.SessionOptions.AdditionalProperties is not null)
        {
            foreach (var propertyKey in this._agentOptions.SessionOptions.AdditionalProperties.Keys)
            {
                _ = requestOptions.AdditionalProperties.TryAdd(propertyKey, this._agentOptions.SessionOptions.AdditionalProperties[propertyKey]);
            }
        }
        else
        {
            requestOptions.AdditionalProperties ??= this._agentOptions.SessionOptions.AdditionalProperties?.Clone();
        }

        if (_agentOptions.SessionOptions.Tools is { Count: not 0 })
        {
            if (requestOptions.Tools is not { Count: > 0 })
            {
                requestOptions.Tools = EnsureDistinctTools(null, _agentOptions.SessionOptions.Tools);
            }
            else
            {
                requestOptions.Tools = EnsureDistinctTools(requestOptions.Tools, _agentOptions.SessionOptions.Tools);
            }
        }
        else if (requestOptions.Tools is { Count: > 0 })
        {
            requestOptions.Tools = EnsureDistinctTools(requestOptions.Tools, null);
        }

        return requestOptions;
    }



    [LoggerMessage(
    Level = LogLevel.Warning,
    Message = "Exception processing incoming messages for AIAgent {Agent}"
    )]
    private static partial void LogWarningExceptionProcessingIncomingMessages(ILogger logger, Exception exception, AIAgent agent);
    #endregion
    private static List<AITool> EnsureDistinctTools(IList<AITool>? current, IEnumerable<AITool>? additions)
    {
        var list = current is null ? new List<AITool>() : (current is List<AITool> l ? l : current.ToList());
        var seen = new HashSet<string>(list.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        if (additions is not null)
        {
            foreach (var tool in additions)
            {
                if (tool?.Name is null)
                {
                    continue;
                }

                if (seen.Add(tool.Name))
                {
                    list.Add(tool);
                }
            }
        }

        return list;
    }

    // Temporary fix for ContinuationToken type issues https://github.com/microsoft/agent-framework/commit/a89c15d6e64f5d7719aa5d353e95143a21ec382b#diff-16ead8c6c074d7276aa5acffbfad81539fa9449d7de193f82b9011803b3d6a3d

    private static AgentRunResponse ToAgentRunResponse(
        IEnumerable<AgentRunResponseUpdate> updates, string agentId)
    {
        _ = Throw.IfNull(updates);

        var chatResponse = updates.Select(u => AsChatResponseUpdate(u)).ToChatResponse();


        return new AgentRunResponse()
        {
            AgentId = agentId,
            AdditionalProperties = chatResponse.AdditionalProperties,
            CreatedAt = chatResponse.CreatedAt,
            Messages = chatResponse.Messages,
            RawRepresentation = chatResponse,
            ResponseId = chatResponse.ResponseId,
            Usage = chatResponse.Usage
        };
    }
    private static ChatResponseUpdate AsChatResponseUpdate(AgentRunResponseUpdate responseUpdate)
    {
        Throw.IfNull(responseUpdate);
        return
            responseUpdate.RawRepresentation as ChatResponseUpdate ??
            new()
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
}
