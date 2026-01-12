using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;

namespace Agents.AI.RealtimeVoice;

/// <summary>
/// 
/// </summary>
/// <remarks>
/// A <see cref="TranscriptTrackingAgentThread"/> is an <see cref="AgentThread"/> that is associated with a specific conversation session.
/// For example, the OpenAI Realtime API supports the concept of conversation sessions, where the conversations state is maintained in a session, however the session may need to be re-established with the transcript of the previous session.
/// For now a ConversationSessionThread will be the state of a specific session, and if the session is lost, the thread can be used to re-establish the session with the previous messages.
/// </remarks>
public class TranscriptTrackingAgentThread : AgentThread
{

    public TranscriptTrackingAgentThread()
    {
        MessageTranscriptStore = new InMemoryChatMessageStore();
    }

    internal TranscriptTrackingAgentThread(
        JsonElement serializedThreadState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        Func<JsonElement, JsonSerializerOptions?, ChatMessageStore>? chatMessageStoreFactory = null,
        Func<JsonElement, JsonSerializerOptions?, AIContextProvider>? aiContextProviderFactory = null)
    {
        if (serializedThreadState.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The serialized thread state must be a JSON object.", nameof(serializedThreadState));
        }

        var state = serializedThreadState.Deserialize(
            AgentAbstractionsJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ConversationSessionThreadState))) as ConversationSessionThreadState;

        this.AIContextProvider = aiContextProviderFactory?.Invoke(state?.AIContextProviderState ?? default, jsonSerializerOptions);

        this.MessageTranscriptStore =
            chatMessageStoreFactory?.Invoke(state?.StoreState ?? default, jsonSerializerOptions) ??
            new InMemoryChatMessageStore(state?.StoreState ?? default, jsonSerializerOptions); // default to an in-memory store
    }

    // ConversationSessions are ephemeral
    //public virtual string? ActiveSessionId { get; set; }

    public ChatMessageStore MessageTranscriptStore { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="AIContextProvider"/> used by this thread to provide additional context to the AI model before each invocation.
    /// </summary>
    public AIContextProvider? AIContextProvider { get; internal set; }

    public async Task UpdateTranscriptMessagesAsync(IEnumerable<ChatMessage> newMessages, CancellationToken cancellationToken = default)
    {
        // Add only text content and strip data contents. This is purely the transcript of the session, and we don't want to store binary data in it.
        var messages = newMessages.Where(m => !string.IsNullOrWhiteSpace(m.Text)).Select(msg => new ChatMessage()
        {
            Role = msg.Role,
            MessageId = msg.MessageId,
            CreatedAt = msg.CreatedAt,
            AuthorName = msg.AuthorName,
            AdditionalProperties = msg.AdditionalProperties,
            Contents = [.. msg.Contents.Where(c => c is not DataContent)]
        });

        await this.MessageTranscriptStore.AddMessagesAsync(messages, cancellationToken).ConfigureAwait(false);
    }


    /// <inheritdoc/>
    public override JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        JsonElement? storeState = this.MessageTranscriptStore.Serialize(jsonSerializerOptions);

        JsonElement? aiContextProviderState = this.AIContextProvider is null ?
            null :
            this.AIContextProvider.Serialize(jsonSerializerOptions);

        var state = new ConversationSessionThreadState
        {
            StoreState = storeState,
            AIContextProviderState = aiContextProviderState
        };

        return JsonSerializer.SerializeToElement(state, AgentsJsonContext.DefaultOptions.GetTypeInfo(typeof(ConversationSessionThreadState)));
    }
    internal sealed class ConversationSessionThreadState
    {
        public JsonElement? StoreState { get; set; }

        public JsonElement? AIContextProviderState { get; set; }
    }
}
