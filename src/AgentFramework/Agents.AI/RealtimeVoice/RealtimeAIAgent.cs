//using System.Runtime.CompilerServices;
//using System.Text.Json;
//using Extensions.AI.Contents;
//using Extensions.AI.RealtimeVoice;
//using Microsoft.Agents.AI;
//using Microsoft.Extensions.AI;
//using Microsoft.Extensions.Logging;
//using Microsoft.Extensions.Logging.Abstractions;
//using Microsoft.Shared.Diagnostics;
//using System;

//namespace Agents.AI.RealtimeVoice;

//public partial class RealtimeAIAgent : AIAgent, IRealtimeAIAgent
//{
//    private readonly RealtimeAgentOptions _agentOptions;


//    public RealtimeAIAgent(
//        ILiveConversationClient client,
//        RealtimeAgentOptions? agentOptions = null,
//        ILoggerFactory? loggerFactory = null,
//        IServiceProvider? services = null) : base()
//    {
//        Client = client.WithDefaultAgentMiddleware(_agentOptions, services);
//        Logger = (loggerFactory
//                      ?? client.GetService<ILoggerFactory>()
//                      ?? NullLoggerFactory.Instance).CreateLogger(GetType());
//        _agentMeta = new AIAgentMetadata(client.Metadata.ProviderName);
//        _agentOptions = agentOptions ?? new RealtimeAgentOptions();
//    }


//    // Configuration
//    protected ILiveConversationClient Client { get; }
//    protected ILogger Logger { get; }
//    private readonly AIAgentMetadata _agentMeta;


//    /// <summary>
//    /// Gets the list of <see cref="AIContextProvider"/> instances used by this agent, to support cases where additional context is needed for each agent run.
//    /// </summary>
//    /// <remarks>
//    /// This property may be null in case no additional context providers were configured.
//    /// </remarks>
//    public IReadOnlyList<AIContextProvider>? AIContextProviders { get; }

//    #region Public Surface

//    public virtual Task SendAudioToRunAsync(
//        DataContent audio,
//        AgentSession session,
//        CancellationToken cancellationToken = default)
//    {
//        var liveThread = EnsureConversationSession(session);
//        return liveThread.Session.SendAudioAsync(audio.Data, cancellationToken);
//    }

//    public virtual Task SendMessagesToRunAsync(
//        IEnumerable<ChatMessage> messages,
//        AgentSession session,
//        CancellationToken cancellationToken = default)
//    {
//        var liveThread = EnsureConversationSession(session);
//        return liveThread.Session.SendMessagesAsync(messages, cancellationToken);
//    }

//    public virtual async Task<LiveConversationAgentSession> GetNewSessionAsync(CancellationToken cancellationToken = default)
//    {
//        var session = await Client.GetSessionAsync(
//            _agentOptions.SessionOptions,
//            cancellationToken).ConfigureAwait(false);

//        var session = new LiveConversationAgentSession(session);
//        return session;
//    }

//    #endregion

//    #region AIAgent overrides
//    public override string Id => _agentOptions.Id ?? Guid.NewGuid().ToString();
//    public override string Name => _agentOptions.Name ?? "RealtimeAIAgent";
//    public override string? Description => _agentOptions.Description;

//    public string? Instructions => _agentOptions?.Instructions;

//    public virtual Task<AgentRunResponse?> CancelRunAsync(string id, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
//    {
//        return Task.FromResult<AgentRunResponse?>(null);
//    }

//    public virtual Task<AgentRunResponse?> DeleteRunAsync(string id, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
//    {
//        return Task.FromResult<AgentRunResponse?>(null);
//    }
//    /// <summary>
//    /// This method creates a realtime session and streams updates until completion, cancellation, or the TerminationPredicate is met.
//    /// If the underlying session returns responses indefinitely, make sure to provide a TerminationPredicate in the AgentRunOptions.
//    /// </summary>
//    /// <param name="messages"></param>
//    /// <param name="session"></param>
//    /// <param name="options"></param>
//    /// <param name="cancellationToken"></param>
//    /// <returns></returns>
//    public override Task<AgentRunResponse> RunAsync(IEnumerable<ChatMessage> messages,
//                                                    AgentSession? session = null,
//                                                    AgentRunOptions? options = null,
//                                                    CancellationToken cancellationToken = default)
//        => RunStreamingAsync(messages, session, options, cancellationToken)
//           .ToAgentRunResponseAsync(cancellationToken);

//    /// <summary>
//    /// Creates or reuses a realtime session and streams updates until:
//    /// 1. The caller cancels via cancellationToken.
//    /// 2. LiveVoiceAgentRunOptions.TerminationPredicate returns true for an update.
//    /// 3. The agent is disposed of
//    /// </summary>
//    /// <param name="messages"></param>
//    /// <param name="session"></param>
//    /// <param name="options"></param>
//    /// <param name="cancellationToken"></param>
//    /// <returns></returns>
//    public override async IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(
//        IEnumerable<ChatMessage> messages,
//        AgentSession? session = null,
//        AgentRunOptions? options = null,
//        [EnumeratorCancellation] CancellationToken cancellationToken = default)
//    {
//        var runOptions = options as RealtimeAgentRunOptions ?? new();
//        session ??= await GetNewSessionAsync(cancellationToken);
//        var typedThread = EnsureConversationSession(session);


//        var (sessionOptions, sessionMessages) =
//            await ConfigureThreadAndSessionAsync(typedThread, runOptions, messages, cancellationToken);

//        var activeSession = typedThread.Session;
//        List<AgentRunResponseUpdate> finishedResponses = [];
//        IAsyncEnumerator<ChatResponseUpdate> responseUpdatesEnumerator;

//        try
//        {
//            if (runOptions.InitiateConversation)
//            {
//                await activeSession.StartResponseAsync(null, cancellationToken).ConfigureAwait(false);
//            }

//            responseUpdatesEnumerator = activeSession.GetStreamingResponseAsync(null, cancellationToken).GetAsyncEnumerator(cancellationToken);
//        }
//        catch (Exception ex)
//        {
//            await NotifyAIContextProviderOfFailureAsync(
//                typedThread, ex, sessionMessages, cancellationToken).ConfigureAwait(false);
//            throw;
//        }

//        var hasUpdates = await responseUpdatesEnumerator.MoveNextAsync().ConfigureAwait(false);

//        //await activeSession.StartResponseAsync(null, cancellationToken).ConfigureAwait(false);

//        while (hasUpdates)
//        {
//            var delta = responseUpdatesEnumerator.Current;

//            if(delta is null)
//            {
//                hasUpdates = await responseUpdatesEnumerator.MoveNextAsync().ConfigureAwait(false);
//                continue;
//            }

//            // There seems to be an Issue with ResponseToken types. Maybe fixed by https://github.com/microsoft/agent-framework/commit/a89c15d6e64f5d7719aa5d353e95143a21ec382b#diff-16ead8c6c074d7276aa5acffbfad81539fa9449d7de193f82b9011803b3d6a3d
//            // Once fixed, AgentRunResponseUpdate can be directly constructed from delta, with additional Agent data added.
//            delta.ContinuationToken = ResponseContinuationToken.FromBytes(new byte[] { 0x01 }); // Dummy token to indicate continuation
//            var wrapped = new AgentRunResponseUpdate()
//            {
//                AgentId = Id,
//                AuthorName = delta.AuthorName ?? Name,
//                RawRepresentation = delta,
                
//                AdditionalProperties = delta.AdditionalProperties,
//                Contents = delta.Contents,
//                CreatedAt = delta.CreatedAt,
//                MessageId = delta.MessageId,
//                ResponseId = delta.ResponseId,
//                Role = delta.Role,
//                ContinuationToken = delta.ContinuationToken,
//            };

//            yield return wrapped;

//            finishedResponses.Add(wrapped);

//            foreach (var content in delta.Contents)
//            {
//                //if (content is RealtimeVadContent vc && vc.VadEvent == VadEventType.InputSpeechEnded)
//                //{
//                //    await activeSession.StartResponseAsync(nextTurnResponseOptions, cancellationToken).ConfigureAwait(false);
//                //}
                
//                if (content is RealtimeResponseFinishedContent)
//                {
//                    await HandleAgentTurnEndedAsync(typedThread, finishedResponses, cancellationToken).ConfigureAwait(false);
//                    finishedResponses.Clear();
//                }
//            }

//            if (runOptions?.TerminationPredicate?.Invoke(wrapped) ?? false)
//            {
//                break;
//            }


//            hasUpdates = await responseUpdatesEnumerator.MoveNextAsync().ConfigureAwait(false);
//        }

//    }


//    public override object? GetService(Type serviceType, object? serviceKey = null)
//        => base.GetService(serviceType, serviceKey) ??
//        (serviceType == typeof(AIAgentMetadata) ? _agentMeta :
//        serviceType == typeof(RealtimeAgentOptions) ? _agentOptions :
//        serviceType.IsInstanceOfType(this) ? this :
//        serviceType.IsInstanceOfType(typeof(IRealtimeAIAgent)) ? this :
//        serviceType == typeof(ILiveConversationClient) ? Client :
//        Client.GetService(serviceType, serviceKey));

//    public override LiveConversationAgentSession GetNewThread() => new(Client.GetSession(_agentOptions.SessionOptions))
//    {
//        MessageTranscriptStore = _agentOptions.ChatMessageStoreFactory?.Invoke(
//            new() { SerializedState = default, JsonSerializerOptions = null }) ?? new InMemoryChatMessageStore(),
//        AIContextProvider = _agentOptions.AIContextProviderFactory?.Invoke(
//            new() { SerializedState = default, JsonSerializerOptions = null }),
//    };

//    public override AgentSession DeserializeThread(
//        JsonElement serializedThread,
//        JsonSerializerOptions? jsonSerializerOptions = null)
//    {
//        var transcriptThread = new TranscriptTrackingAgentSession(serializedThread, jsonSerializerOptions);
//        // Create new session with deserialized state
//        var session = new LiveConversationAgentSession(
//            Client.GetSession(_agentOptions.SessionOptions),
//            serializedThread,
//            jsonSerializerOptions
//            );

//        return session;
//    }
//    #endregion

//    #region Private Methods

//    private static LiveConversationAgentSession EnsureConversationSession(AgentSession? session)
//    {
//        if (session is not LiveConversationAgentSession liveThread)
//        {
//            throw new InvalidOperationException(
//                "The provided session is not compatible with this agent. " +
//                "Use GetNewThread() to create a compatible session.");
//        }
//        return liveThread;
//    }

//    private async Task HandleAgentTurnEndedAsync(
//        LiveConversationAgentSession session,
//        List<AgentRunResponseUpdate> finishedResponses,
//        CancellationToken cancellationToken)
//    {
//        if (finishedResponses is { Count: 0 }) return;
//        var agentResponse = ToAgentRunResponse(finishedResponses, Id);

//        await session.UpdateTranscriptMessagesAsync(agentResponse.Messages, cancellationToken).ConfigureAwait(false);
//        await NotifyAIContextProviderOfSuccessAsync(
//            session,
//            agentResponse.Messages.Where(m => m.Role == ChatRole.User),
//            agentResponse.Messages,
//            cancellationToken).ConfigureAwait(false);
//    }

//    /// <summary>
//    /// Notify the <see cref="AIContextProvider"/> when an agent run succeeded, if there is an <see cref="AIContextProvider"/>.
//    /// </summary>
//    private static async ValueTask NotifyAIContextProviderOfSuccessAsync(TranscriptTrackingAgentSession session, IEnumerable<ChatMessage> inputMessages, IEnumerable<ChatMessage> responseMessages, CancellationToken cancellationToken)
//    {
//        if (session.AIContextProvider is not null)
//        {
//            await session.AIContextProvider.InvokedAsync(new(inputMessages, null) { ResponseMessages = responseMessages },
//                cancellationToken).ConfigureAwait(false);
//        }
//    }

//    /// <summary>
//    /// Notify the <see cref="AIContextProvider"/> of any failure during an agent run, if there is an <see cref="AIContextProvider"/>.
//    /// </summary>
//    private static async ValueTask NotifyAIContextProviderOfFailureAsync(TranscriptTrackingAgentSession session, Exception ex, IEnumerable<ChatMessage> inputMessages, CancellationToken cancellationToken)
//    {
//        if (session.AIContextProvider is not null)
//        {
//            await session.AIContextProvider.InvokedAsync(new(inputMessages, null) { InvokeException = ex },
//                cancellationToken).ConfigureAwait(false);
//        }
//    }

//    private static async Task<AIContext?> GetAIContextForNextInvocation(TranscriptTrackingAgentSession session, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
//    {
//        if (session.AIContextProvider is not { } provider) return null;

//        var invokingContext = new AIContextProvider.InvokingContext(messages);

//        return await provider.InvokingAsync(invokingContext, cancellationToken);
//    }

//    private static ILiveConversationClient ApplyRunOptionsTransformationsToClient(RealtimeAgentRunOptions? options, ILiveConversationClient conversationClient)
//    {
//        if (options?.ConversationClientFactory is not null)
//        {
//            // If we have a custom chat client factory, we should use it to create a new chat client with the transformed tools.
//            conversationClient = options.ConversationClientFactory(conversationClient);
//            _ = Throw.IfNull(conversationClient);
//        }

//        return conversationClient;
//    }


//    private async Task<(LiveConversationSessionOptions? sessionOptions, IEnumerable<ChatMessage> messages)> ConfigureThreadAndSessionAsync(
//               LiveConversationAgentSession session,
//               RealtimeAgentRunOptions? runOptions,
//               IEnumerable<ChatMessage> initialMessages,
//               CancellationToken cancellationToken)
//    {
//        var sessionOptions = GetSessionOptions(runOptions);

//        var client = ApplyRunOptionsTransformationsToClient(runOptions, Client);

//        await session._sessionGate.WaitAsync(cancellationToken);
//        try
//        {
//            // Load history and context
//            List<ChatMessage> sessionHistory = [];

//            if (session.Session.State == RealtimeSessionState.None && session.MessageTranscriptStore is not null)
//            {
//                // We have not started the Session yet, so we can load previous messages.
//                var previousSessionMessages = await session.MessageTranscriptStore.GetMessagesAsync(cancellationToken)
//                        .ConfigureAwait(false);

//                sessionHistory.AddRange(previousSessionMessages);
//            }

//            var aiContext = await GetAIContextForNextInvocation(session, initialMessages, cancellationToken)
//                .ConfigureAwait(false);

//            if (aiContext?.Messages is { Count: > 0 })
//            {
//                sessionHistory.AddRange(aiContext.Messages);
//            }

//            if (aiContext?.Tools is { Count: > 0 })
//            {
//                sessionOptions ??= new();
//                sessionOptions.Tools ??= [];

//                foreach (var tool in aiContext.Tools.OfType<AIFunction>())
//                {
//                    sessionOptions.Tools.Add(tool);
//                }
//            }

//            if (aiContext?.Instructions is not null)
//            {
//                sessionOptions ??= new();
//                sessionOptions.Instructions = string.IsNullOrWhiteSpace(sessionOptions.Instructions)
//                    ? aiContext.Instructions
//                    : $"{sessionOptions.Instructions}{Environment.NewLine}{aiContext.Instructions}";
//            }

//            if (!string.IsNullOrWhiteSpace(Instructions))
//            {
//                sessionOptions ??= new();
//                sessionOptions.Instructions = string.IsNullOrWhiteSpace(sessionOptions.Instructions) ? Instructions : $"{Instructions}{Environment.NewLine}{sessionOptions.Instructions}";
//            }
//            sessionHistory.AddRange(initialMessages);


//            // Reuse existing session if still valid
//            if (session.Session is null or { State: RealtimeSessionState.Closed or RealtimeSessionState.Closing or RealtimeSessionState.Error })
//            {
//                session.Session?.Dispose();

//                session.Session = await client.GetSessionAsync(
//                    sessionOptions,
//                    cancellationToken);

//            }

//            if (sessionOptions is not null)
//            {
//                await session.Session.ConfigureSessionAsync(sessionOptions, cancellationToken);
//            }

//            await SendMessagesToRunAsync(sessionHistory, session, cancellationToken);

//            return (sessionOptions, sessionHistory);
//        }
//        finally { session._sessionGate.Release(); }
//    }

//    private LiveConversationSessionOptions? GetSessionOptions(RealtimeAgentRunOptions? runOptions = null)
//    {
//        var requestOptions = runOptions?.SessionOptions?.Clone();

//        if (_agentOptions.SessionOptions is null)
//        {
//            return requestOptions;
//        }

//        if (requestOptions is null)
//        {
//            return null;
//        }

//        // Combine options, giving precedence to requestOptions
//        requestOptions.Instructions ??= _agentOptions.SessionOptions.Instructions;
//        requestOptions.TurnDetection ??= _agentOptions.SessionOptions.TurnDetection;
//        requestOptions.Voice ??= _agentOptions.SessionOptions.Voice;
//        requestOptions.InputAudioFormat ??= _agentOptions.SessionOptions.InputAudioFormat;
//        requestOptions.OutputAudioFormat ??= _agentOptions.SessionOptions.OutputAudioFormat;
//        requestOptions.InputTranscription ??= _agentOptions.SessionOptions.InputTranscription;
//        requestOptions.ToolMode ??= _agentOptions.SessionOptions.ToolMode;
//        //requestOptions.Tools ??= _agentOptions.SessionOptions.Tools;
//        requestOptions.Modalities ??= _agentOptions.SessionOptions.Modalities;
//        requestOptions.MaxOutputTokens ??= _agentOptions.SessionOptions.MaxOutputTokens;

//        if (requestOptions.AdditionalProperties is not null && _agentOptions.SessionOptions.AdditionalProperties is not null)
//        {
//            foreach (var propertyKey in _agentOptions.SessionOptions.AdditionalProperties.Keys)
//            {
//                _ = requestOptions.AdditionalProperties.TryAdd(propertyKey, _agentOptions.SessionOptions.AdditionalProperties[propertyKey]);
//            }
//        }
//        else
//        {
//            requestOptions.AdditionalProperties ??= _agentOptions.SessionOptions.AdditionalProperties?.Clone();
//        }

//        if (_agentOptions.SessionOptions.Tools is { Count: > 0 })
//        {
//            if (requestOptions.Tools is { Count: 0 })
//            {
//                // If no tools were specified in the request, use the agent's default tools.
//                requestOptions.Tools = _agentOptions.SessionOptions.Tools;
//            }
//            else
//            {
//                // Merge tools from both the request and the agent, ensuring no duplicates.
//                requestOptions.Tools =  EnsureDistinctTools(requestOptions.Tools, _agentOptions.SessionOptions.Tools);
//            }
//        }

//        return requestOptions;
//    }



//    [LoggerMessage(
//    Level = LogLevel.Warning,
//    Message = "Exception processing incoming messages for AIAgent {Agent}"
//    )]
//    private static partial void LogWarningExceptionProcessingIncomingMessages(ILogger logger, Exception exception, AIAgent agent);
//    #endregion
//    private static List<AITool> EnsureDistinctTools(IList<AITool>? current, IEnumerable<AITool>? additions)
//    {
//        var list = current is null ? new List<AITool>() : (current is List<AITool> l ? l : current.ToList());
//        var seen = new HashSet<string>(list.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

//        if (additions is not null)
//        {
//            foreach (var tool in additions)
//            {
//                if (tool?.Name is null)
//                {
//                    continue;
//                }

//                if (seen.Add(tool.Name))
//                {
//                    list.Add(tool);
//                }
//            }
//        }

//        return list;
//    }

//    // Temporary fix for ContinuationToken type issues https://github.com/microsoft/agent-framework/commit/a89c15d6e64f5d7719aa5d353e95143a21ec382b#diff-16ead8c6c074d7276aa5acffbfad81539fa9449d7de193f82b9011803b3d6a3d

//    private static AgentRunResponse ToAgentRunResponse(
//        IEnumerable<AgentRunResponseUpdate> updates, string agentId)
//    {
//        _ = Throw.IfNull(updates);

//        var chatResponse = updates.Select(u => AsChatResponseUpdate(u)).ToChatResponse();


//        return new AgentRunResponse()
//        {
//            AgentId = agentId,
//            AdditionalProperties = chatResponse.AdditionalProperties,
//            CreatedAt = chatResponse.CreatedAt,
//            Messages = chatResponse.Messages,
//            RawRepresentation = chatResponse,
//            ResponseId = chatResponse.ResponseId,
//            Usage = chatResponse.Usage
//        };
//    }
//    private static ChatResponseUpdate AsChatResponseUpdate(AgentRunResponseUpdate responseUpdate)
//    {
//        Throw.IfNull(responseUpdate);
//        return
//            responseUpdate.RawRepresentation as ChatResponseUpdate ??
//            new()
//            {
//                AdditionalProperties = responseUpdate.AdditionalProperties,
//                AuthorName = responseUpdate.AuthorName,
//                Contents = responseUpdate.Contents,
//                CreatedAt = responseUpdate.CreatedAt,
//                MessageId = responseUpdate.MessageId,
//                RawRepresentation = responseUpdate,
//                ResponseId = responseUpdate.ResponseId,
//                Role = responseUpdate.Role,
//            };
//    }
//}
