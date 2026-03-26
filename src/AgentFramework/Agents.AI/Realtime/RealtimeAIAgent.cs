using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Shared.Diagnostics;

namespace Agents.AI.Realtime;

/// <summary>
/// Provides an <see cref="AIAgent"/> that delegates to an <see cref="IRealtimeClient"/> implementation
/// for real-time, bidirectional AI interactions.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="RealtimeAIAgent"/> bridges the <see cref="AIAgent"/> abstraction with real-time
/// streaming protocols. Unlike <see cref="ChatClientAgent"/>, which uses request/response style interactions,
/// this agent maintains persistent sessions via <see cref="IRealtimeClientSession"/> for continuous
/// bidirectional communication.
/// </para>
/// <para>
/// <strong>Security considerations:</strong> The <see cref="RealtimeAIAgent"/> orchestrates data flow
/// across trust boundaries. The underlying AI service is an external endpoint and real-time responses
/// should be treated as untrusted output. Developers should validate and sanitize output before rendering
/// it in HTML, executing it as code, or passing it to any security-sensitive context.
/// </para>
/// </remarks>
public sealed class RealtimeAIAgent : AIAgent
{
    private readonly RealtimeAgentOptions? _agentOptions;
    private readonly AIAgentMetadata _agentMetadata;
    private readonly ILogger _logger;
    private readonly HashSet<string> _aiContextProviderStateKeys;

    /// <summary>
    /// Initializes a new instance of the <see cref="RealtimeAIAgent"/> class.
    /// </summary>
    /// <param name="realtimeClient">The realtime client to use for creating sessions.</param>
    /// <param name="options">Optional configuration options for the agent.</param>
    /// <param name="loggerFactory">Optional logger factory for creating loggers used by the agent.</param>
    /// <exception cref="ArgumentNullException"><paramref name="realtimeClient"/> is <see langword="null"/>.</exception>
    public RealtimeAIAgent(
        IRealtimeClient realtimeClient,
        RealtimeAgentOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        _ = Throw.IfNull(realtimeClient);

        RealtimeClient = realtimeClient;
        _agentOptions = options?.Clone();
        _agentMetadata = new AIAgentMetadata("realtime");
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RealtimeAIAgent>();
        ChatHistoryProvider = _agentOptions?.ChatHistoryProvider ?? new InMemoryChatHistoryProvider();
        AIContextProviders = _agentOptions?.AIContextProviders as IReadOnlyList<AIContextProvider> ?? _agentOptions?.AIContextProviders?.ToList();

        _aiContextProviderStateKeys = ValidateAndCollectStateKeys(_agentOptions?.AIContextProviders, ChatHistoryProvider);
    }

    /// <summary>
    /// Gets the underlying <see cref="IRealtimeClient"/> used by this agent to create realtime sessions.
    /// </summary>
    public IRealtimeClient RealtimeClient { get; }

    /// <summary>
    /// Gets the <see cref="ChatHistoryProvider"/> used by this agent, to support cases where the chat history is not stored by the agent service.
    /// </summary>
    /// <remarks>
    /// This property may be null in case the agent stores messages in the underlying agent service.
    /// </remarks>
    public ChatHistoryProvider? ChatHistoryProvider { get; private set; }

    /// <summary>
    /// Gets the list of <see cref="AIContextProvider"/> instances used by this agent, to support cases where additional context is needed for each agent run.
    /// </summary>
    /// <remarks>
    /// This property may be null in case no additional context providers were configured.
    /// </remarks>
    public IReadOnlyList<AIContextProvider>? AIContextProviders { get; }

    /// <inheritdoc/>
    protected override string? IdCore => _agentOptions?.Id;

    /// <inheritdoc/>
    public override string? Name => _agentOptions?.Name;

    /// <inheritdoc/>
    public override string? Description => _agentOptions?.Description;

    /// <summary>
    /// Gets the default <see cref="RealtimeSessionOptions"/> configured for this agent.
    /// </summary>
    public RealtimeSessionOptions? SessionOptions => _agentOptions?.SessionOptions;

    /// <summary>
    /// Creates a new realtime session via the underlying <see cref="IRealtimeClient"/> and returns
    /// an <see cref="RealtimeAIAgentSession"/> that wraps it.
    /// </summary>
    /// <param name="sessionOptions">Optional session options that override the agent's defaults.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="RealtimeAIAgentSession"/> containing the active realtime session.</returns>
    public async ValueTask<RealtimeAIAgentSession> CreateRealtimeSessionAsync(
        RealtimeSessionOptions? sessionOptions = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveOptions = sessionOptions ?? _agentOptions?.SessionOptions;

        var loggingAgentName = GetLoggingAgentName();

        _logger.LogDebug("Creating realtime session for agent '{AgentName}' (Id: {AgentId})", loggingAgentName, Id);

        var clientSession = await RealtimeClient.CreateSessionAsync(effectiveOptions, cancellationToken).ConfigureAwait(false);

        var session = new RealtimeAIAgentSession
        {
            ClientSession = clientSession,
        };

        _logger.LogDebug("Realtime session created for agent '{AgentName}' (Id: {AgentId})", loggingAgentName, Id);

        return session;
    }

    /// <summary>
    /// Sends a client message to the realtime session.
    /// </summary>
    /// <param name="session">The agent session containing the active realtime client session.</param>
    /// <param name="message">The client message to send.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The session does not have an active realtime client session.</exception>
    public async Task SendAsync(
        RealtimeAIAgentSession session,
        RealtimeClientMessage message,
        CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(session);
        _ = Throw.IfNull(message);

        var clientSession = session.ClientSession
            ?? throw new InvalidOperationException("The session does not have an active realtime client session. Call CreateRealtimeSessionAsync first.");

        _logger.LogDebug("Sending message to realtime session for agent '{AgentName}' (Id: {AgentId})", GetLoggingAgentName(), Id);

        await clientSession.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams server messages from the realtime session as <see cref="AgentResponseUpdate"/> instances.
    /// </summary>
    /// <param name="session">The agent session containing the active realtime client session.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An async enumerable of <see cref="AgentResponseUpdate"/> instances from the realtime session.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The session does not have an active realtime client session.</exception>
    public async IAsyncEnumerable<AgentResponseUpdate> GetStreamingResponseAsync(
        RealtimeAIAgentSession session,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(session);

        var clientSession = session.ClientSession
            ?? throw new InvalidOperationException("The session does not have an active realtime client session. Call CreateRealtimeSessionAsync first.");

        _logger.LogDebug("Starting streaming response from realtime session for agent '{AgentName}' (Id: {AgentId})", GetLoggingAgentName(), Id);

        await foreach (var serverMessage in clientSession.GetStreamingResponseAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new AgentResponseUpdate
            {
                AuthorName = Name,
                AgentId = Id,
                RawRepresentation = serverMessage,
            };
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The <see cref="RealtimeAIAgent"/> does not support the standard request/response <see cref="AIAgent.RunAsync"/>
    /// pattern. Use <see cref="CreateRealtimeSessionAsync"/>, <see cref="SendAsync"/>, and
    /// <see cref="GetStreamingResponseAsync"/> instead.
    /// </remarks>
    /// <exception cref="NotSupportedException">Always thrown. Use the realtime session APIs instead.</exception>
    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            $"{nameof(RealtimeAIAgent)} does not support the standard RunAsync pattern. " +
            $"Use {nameof(CreateRealtimeSessionAsync)}, {nameof(SendAsync)}, and {nameof(GetStreamingResponseAsync)} for realtime interactions.");
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The <see cref="RealtimeAIAgent"/> does not support the standard streaming <see cref="AIAgent.RunStreamingAsync"/>
    /// pattern. Use <see cref="CreateRealtimeSessionAsync"/>, <see cref="SendAsync"/>, and
    /// <see cref="GetStreamingResponseAsync"/> instead.
    /// </remarks>
    /// <exception cref="NotSupportedException">Always thrown. Use the realtime session APIs instead.</exception>
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        var runOptions = options as RealtimeAgentRunOptions ?? new();

        var safeSession = EnsureConversationSession(session);

        var clientSession = safeSession.ClientSession
    ?? throw new InvalidOperationException("The session does not have an active realtime client session. Call CreateRealtimeSessionAsync first.");

        _logger.LogDebug("Starting streaming response from realtime session for agent '{AgentName}' (Id: {AgentId})", GetLoggingAgentName(), Id);
        List<ChatResponseUpdate> responseUpdates = GetResponseUpdates(continuationToken);

        await foreach (var serverMessage in clientSession.GetStreamingResponseAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new AgentResponseUpdate
            {
                AuthorName = Name,
                AgentId = Id,
                RawRepresentation = serverMessage,
            };
        }
    }

    /// <inheritdoc/>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        _ = Throw.IfNull(serviceType);

        return base.GetService(serviceType, serviceKey)
            ?? (serviceKey is null && serviceType == typeof(AIAgentMetadata) ? _agentMetadata
            : serviceType == typeof(IRealtimeClient) ? RealtimeClient
            : serviceType == typeof(RealtimeAgentOptions) ? _agentOptions
            : this.AIContextProviders?.Select(provider => provider.GetService(serviceType, serviceKey)).FirstOrDefault(s => s is not null)
            ?? this.ChatHistoryProvider?.GetService(serviceType, serviceKey)
            ?? RealtimeClient.GetService(serviceType, serviceKey));
    }

    /// <inheritdoc/>
    protected override async ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        var clientSession = await RealtimeClient.CreateSessionAsync(_agentOptions?.SessionOptions, cancellationToken);
        return new RealtimeAIAgentSession()
        {
            ClientSession = clientSession,
        };
    }

    /// <inheritdoc/>
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (session is not RealtimeAIAgentSession realtimeSession)
        {
            throw new InvalidOperationException(
                $"The provided session is of type '{session.GetType().Name}', but {nameof(RealtimeAIAgent)} requires a session of type '{nameof(RealtimeAIAgentSession)}'.");
        }

        return new(realtimeSession.Serialize(jsonSerializerOptions));
    }

    /// <inheritdoc/>
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        return new(RealtimeAIAgentSession.Deserialize(serializedState, jsonSerializerOptions));
    }

    private static RealtimeAIAgentSession EnsureConversationSession(AgentSession? session)
    {
        if (session is not RealtimeAIAgentSession realtimeSession)
        {
            throw new InvalidOperationException(
                "The provided session is not compatible with this agent. " +
                $"Use {nameof(CreateSessionCoreAsync)} to create a compatible session.");
        }

        if(realtimeSession.ClientSession is null)
        {
            throw new InvalidOperationException(
                "The provided session does not have an active realtime client session. " +
                $"Use {nameof(CreateSessionCoreAsync)} to create a session with an active realtime client session.");
        }
        return realtimeSession;
    }


    private static RealtimeAgentContinuationToken? WrapContinuationToken(ResponseContinuationToken? continuationToken, IEnumerable<ChatMessage>? inputMessages = null, List<ChatResponseUpdate>? responseUpdates = null)
    {
        if (continuationToken is null)
        {
            return null;
        }

        return new(continuationToken)
        {
            // Save input messages to the continuation token so they can be added to the session and
            // provided to the context provider in the last successful streaming resumption run.
            // That's necessary for scenarios where initial streaming run is interrupted and streaming is resumed later.
            InputMessages = inputMessages?.Any() is true ? inputMessages : null,

            // Save all updates received so far to the continuation token so they can be provided to the
            // message store and context provider in the last successful streaming resumption run.
            // That's necessary for scenarios where a streaming run is interrupted after some updates were received.
            ResponseUpdates = responseUpdates?.Count > 0 ? responseUpdates : null
        };
    }


    /// <summary>
    /// Notify the <see cref="AIContextProvider"/> when an agent run succeeded, if there is an <see cref="AIContextProvider"/>.
    /// </summary>
    private async ValueTask NotifyAIContextProviderOfSuccessAsync(
        RealtimeAIAgentSession session,
        IEnumerable<ChatMessage> inputMessages,
        IEnumerable<ChatMessage> responseMessages,
        CancellationToken cancellationToken)
    {
        if (this.AIContextProviders is { Count: > 0 } contextProviders)
        {
            AIContextProvider.InvokedContext invokedContext = new(this, session, inputMessages, responseMessages);

            foreach (var contextProvider in contextProviders)
            {
                await contextProvider.InvokedAsync(invokedContext, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Notify the <see cref="AIContextProvider"/> of any failure during an agent run, if there is an <see cref="AIContextProvider"/>.
    /// </summary>
    private async ValueTask NotifyAIContextProviderOfFailureAsync(
        RealtimeAIAgentSession session,
        Exception ex,
        IEnumerable<ChatMessage>? inputMessages = null,
        CancellationToken cancellationToken = default)
    {
        if (this.AIContextProviders is { Count: > 0 } contextProviders)
        {
            AIContextProvider.InvokedContext invokedContext = new(this, session, inputMessages ?? [], ex);

            foreach (var contextProvider in contextProviders)
            {
                await contextProvider.InvokedAsync(invokedContext, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static IEnumerable<ChatMessage> GetInputMessages(IReadOnlyCollection<ChatMessage> inputMessages, RealtimeAgentContinuationToken? token)
    {
        // First, use input messages if provided.
        if (inputMessages.Count > 0)
        {
            return inputMessages;
        }

        // Fallback to messages saved in the continuation token if available.
        return token?.InputMessages ?? [];
    }

    private static List<ChatResponseUpdate> GetResponseUpdates(RealtimeAgentContinuationToken? token)
    {
        // Restore any previously received updates from the continuation token.
        return token?.ResponseUpdates?.ToList() ?? [];
    }

    private string GetLoggingAgentName() => Name ?? "UnnamedAgent";

    /// <summary>
    /// Validates that all configured providers have unique <see cref="AIContextProvider.StateKeys"/> values
    /// and returns a <see cref="HashSet{T}"/> of the AIContextProvider state keys.
    /// </summary>
    private static HashSet<string> ValidateAndCollectStateKeys(IEnumerable<AIContextProvider>? aiContextProviders, ChatHistoryProvider? chatHistoryProvider)
    {
        HashSet<string> stateKeys = new(StringComparer.Ordinal);

        if (aiContextProviders is not null)
        {
            foreach (var provider in aiContextProviders)
            {
                foreach (var key in provider.StateKeys)
                {
                    if (!stateKeys.Add(key))
                    {
                        throw new InvalidOperationException(
                            $"Multiple providers use the same state key '{key}'. Each provider must use a unique state key to avoid overwriting each other's state.");
                    }
                }
            }
        }

        if (chatHistoryProvider is null
            && stateKeys.Contains(nameof(InMemoryChatHistoryProvider)))
        {
            throw new InvalidOperationException(
                $"The default {nameof(InMemoryChatHistoryProvider)} uses the state key '{nameof(InMemoryChatHistoryProvider)}', which is already used by one of the configured AIContextProviders. Each provider must use a unique state key to avoid overwriting each other's state. To resolve this, either configure a different state key for the AIContextProvider that is using '{nameof(InMemoryChatHistoryProvider)}' as its state key, or provide a custom ChatHistoryProvider with a unique state key.");
        }

        if (chatHistoryProvider is not null)
        {
            foreach (var key in chatHistoryProvider.StateKeys)
            {
                if (stateKeys.Contains(key))
                {
                    throw new InvalidOperationException(
                        $"The ChatHistoryProvider '{chatHistoryProvider.GetType().Name}' uses state key '{key}' which is already used by one of the configured AIContextProviders. Each provider must use unique state keys to avoid overwriting each other's state. To resolve this, either configure different state keys for the AIContextProvider that shares keys with the ChatHistoryProvider, or reconfigure the custom ChatHistoryProvider with unique state keys.");
                }
            }
        }

        return stateKeys;
    }


    private async Task<(RealtimeAIAgentSession? session, RealtimeAgentContinuationToken? continuationToken)> ConfigureSessionAsync(
               RealtimeAIAgentSession agentSession,
               RealtimeAgentRunOptions? runOptions,
               IEnumerable<ChatMessage> initialMessages,
               CancellationToken cancellationToken)
    {
        var sessionOptions = GetSessionOptions(runOptions);

        var client = ApplyRunOptionsTransformationsToClient(runOptions, RealtimeClient);
        var clientSession = agentSession.ClientSession ?? throw new InvalidOperationException("The session does not have an active realtime client session. Call CreateRealtimeSessionAsync first.");

        //try
        //{
        //    // Load history and context
        //    List<ChatMessage> sessionHistory = [];

        //    if (aiContext?.Messages is { Count: > 0 })
        //    {
        //        sessionHistory.AddRange(aiContext.Messages);
        //    }

        //    if (aiContext?.Tools is { Count: > 0 })
        //    {
        //        sessionOptions ??= new();
        //        sessionOptions.Tools ??= [];

        //        foreach (var tool in aiContext.Tools.OfType<AIFunction>())
        //        {
        //            sessionOptions.Tools.Add(tool);
        //        }
        //    }

        //    if (aiContext?.Instructions is not null)
        //    {
        //        sessionOptions ??= new();
        //        sessionOptions.Instructions = string.IsNullOrWhiteSpace(sessionOptions.Instructions)
        //            ? aiContext.Instructions
        //            : $"{sessionOptions.Instructions}{Environment.NewLine}{aiContext.Instructions}";
        //    }

        //    if (!string.IsNullOrWhiteSpace(Instructions))
        //    {
        //        sessionOptions ??= new();
        //        sessionOptions.Instructions = string.IsNullOrWhiteSpace(sessionOptions.Instructions) ? Instructions : $"{Instructions}{Environment.NewLine}{sessionOptions.Instructions}";
        //    }
        //    sessionHistory.AddRange(initialMessages);


        //    // Reuse existing session if still valid
        //    if (session.Session is null or { State: RealtimeSessionState.Closed or RealtimeSessionState.Closing or RealtimeSessionState.Error })
        //    {
        //        session.Session?.Dispose();

        //        session.Session = await client.GetSessionAsync(
        //            sessionOptions,
        //            cancellationToken);

        //    }

        //    if (sessionOptions is not null)
        //    {
        //        await session.Session.ConfigureSessionAsync(sessionOptions, cancellationToken);
        //    }

        //    await SendMessagesToRunAsync(sessionHistory, session, cancellationToken);

        //    return (sessionOptions, sessionHistory);
        //}
        //finally { session._sessionGate.Release(); }
    }

    private static IRealtimeClient ApplyRunOptionsTransformationsToClient(RealtimeAgentRunOptions? options, IRealtimeClient conversationClient)
    {
        if (options?.ConversationClientFactory is not null)
        {
            // If we have a custom chat client factory, we should use it to create a new chat client with the transformed tools.
            conversationClient = options.ConversationClientFactory(conversationClient);
            _ = Throw.IfNull(conversationClient);
        }

        return conversationClient;
    }


    private (RealtimeSessionOptions?, RealtimeAgentContinuationToken?) GetSessionOptions(AgentRunOptions? runOptions = null)
    {
        var requestOptions = (runOptions as RealtimeAgentRunOptions)?.SessionOptions;
        if (_agentOptions?.SessionOptions is null)
        {
            return ApplyAgentRunOptionsOverrides(requestOptions, runOptions);
        }

        if (requestOptions is null)
        {
            return ApplyAgentRunOptionsOverrides(_agentOptions?.SessionOptions, runOptions);
        }

        // Combine options, giving precedence to requestOptions
        requestOptions.Instructions ??= _agentOptions.SessionOptions.Instructions;
        requestOptions.TurnDetection ??= _agentOptions.SessionOptions.;
        requestOptions.Voice ??= _agentOptions.SessionOptions.Voice;
        requestOptions.InputAudioFormat ??= _agentOptions.SessionOptions.InputAudioFormat;
        requestOptions.OutputAudioFormat ??= _agentOptions.SessionOptions.OutputAudioFormat;
        requestOptions.InputTranscription ??= _agentOptions.SessionOptions.InputTranscription;
        requestOptions.ToolMode ??= _agentOptions.SessionOptions.ToolMode;
        //requestOptions.Tools ??= _agentOptions.SessionOptions.Tools;
        requestOptions.Modalities ??= _agentOptions.SessionOptions.Modalities;
        requestOptions.MaxOutputTokens ??= _agentOptions.SessionOptions.MaxOutputTokens;

        if (requestOptions.AdditionalProperties is not null && _agentOptions.SessionOptions.AdditionalProperties is not null)
        {
            foreach (var propertyKey in _agentOptions.SessionOptions.AdditionalProperties.Keys)
            {
                _ = requestOptions.AdditionalProperties.TryAdd(propertyKey, _agentOptions.SessionOptions.AdditionalProperties[propertyKey]);
            }
        }
        else
        {
            requestOptions.AdditionalProperties ??= _agentOptions.SessionOptions.AdditionalProperties?.Clone();
        }

        if (_agentOptions.SessionOptions.Tools is { Count: > 0 })
        {
            if (requestOptions.Tools is { Count: 0 })
            {
                // If no tools were specified in the request, use the agent's default tools.
                requestOptions.Tools = _agentOptions.SessionOptions.Tools;
            }
            else
            {
                // Merge tools from both the request and the agent, ensuring no duplicates.
                requestOptions.Tools = EnsureDistinctTools(requestOptions.Tools, _agentOptions.SessionOptions.Tools);
            }
        }

        return requestOptions;

        static (RealtimeSessionOptions?, RealtimeAgentContinuationToken?) ApplyAgentRunOptionsOverrides(RealtimeSessionOptions? realtimeSessionOptions, AgentRunOptions? agentRunOptions)
        {

            RealtimeAgentContinuationToken? agentContinuationToken = null;

            if (agentRunOptions?.ContinuationToken is { } continuationToken)
            {
                agentContinuationToken = RealtimeAgentContinuationToken.FromToken(continuationToken);
                realtimeSessionOptions ??= new RealtimeSessionOptions();
                realtimeSessionOptions.ContinuationToken = agentContinuationToken!.InnerToken;
            }

            // Add/Replace any additional properties from the AgentRunOptions, since they should always take precedence.
            if (agentRunOptions?.AdditionalProperties is { Count: > 0 })
            {
                chatOptions ??= new ChatOptions();
                chatOptions.AdditionalProperties ??= new();
                foreach (var kvp in agentRunOptions.AdditionalProperties)
                {
                    chatOptions.AdditionalProperties[kvp.Key] = kvp.Value;
                }
            }

            return (chatOptions, agentContinuationToken);
        }
    }
}
