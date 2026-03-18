using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
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
    private readonly RealtimeAIAgentOptions? _agentOptions;
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
        RealtimeAIAgentOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        _ = Throw.IfNull(realtimeClient);

        RealtimeClient = realtimeClient;
        _agentOptions = options?.Clone();
        _agentMetadata = new AIAgentMetadata("realtime");
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RealtimeAIAgent>();
        AIContextProviders = _agentOptions?.AIContextProviders as IReadOnlyList<AIContextProvider> ?? _agentOptions?.AIContextProviders?.ToList();

    }

    /// <summary>
    /// Gets the underlying <see cref="IRealtimeClient"/> used by this agent to create realtime sessions.
    /// </summary>
    public IRealtimeClient RealtimeClient { get; }

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

        _logger.LogDebug("Creating realtime session for agent '{AgentName}' (Id: {AgentId})", Name ?? "UnnamedAgent", Id);

        var clientSession = await RealtimeClient.CreateSessionAsync(effectiveOptions, cancellationToken).ConfigureAwait(false);

        var session = new RealtimeAIAgentSession
        {
            ClientSession = clientSession,
        };

        _logger.LogDebug("Realtime session created for agent '{AgentName}' (Id: {AgentId})", Name ?? "UnnamedAgent", Id);

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

        _logger.LogDebug("Sending message to realtime session for agent '{AgentName}' (Id: {AgentId})", Name ?? "UnnamedAgent", Id);

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

        _logger.LogDebug("Starting streaming response from realtime session for agent '{AgentName}' (Id: {AgentId})", Name ?? "UnnamedAgent", Id);

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
    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var typedSession = EnsureConversationSession(session);
        var inputMessages = Throw.IfNull(messages) as IReadOnlyCollection<ChatMessage> ?? [.. messages];

        IAsyncEnumerator<ChatResponseUpdate> responseUpdatesEnumerator;

        throw new NotSupportedException(
            $"{nameof(RealtimeAIAgent)} does not support the standard RunStreamingAsync pattern. " +
            $"Use {nameof(CreateRealtimeSessionAsync)}, {nameof(SendAsync)}, and {nameof(GetStreamingResponseAsync)} for realtime interactions.");
    }

    /// <inheritdoc/>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        _ = Throw.IfNull(serviceType);

        // Check base first (returns 'this' if serviceType matches).
        object? baseResult = base.GetService(serviceType, serviceKey);
        if (baseResult is not null)
        {
            return baseResult;
        }

        // Return the AIAgentMetadata for this agent.
        if (serviceKey is null && serviceType == typeof(AIAgentMetadata))
        {
            return _agentMetadata;
        }

        // Return the underlying IRealtimeClient.
        if (serviceType == typeof(IRealtimeClient))
        {
            return RealtimeClient;
        }

        // Delegate to the underlying IRealtimeClient for any other service requests.
        return RealtimeClient.GetService(serviceType, serviceKey);
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
    /// Notify the <see cref="AIContextProvider"/> of any failure during an agent run, if there is an <see cref="AIContextProvider"/>.
    /// </summary>
    private async ValueTask HandleFailureAsync(RealtimeAIAgentSession session, Exception ex, IEnumerable<ChatMessage>? inputMessages = null, IEnumerable<ChatMessage>? responseMessages = null, CancellationToken cancellationToken = default)
    {
        if (this.AIContextProviders is { Count: > 0 } contextProviders)
        {
            AIContextProvider.InvokedContext invokedContext = new(this, session, inputMessages ?? [], responseMessages ?? []);

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
}
