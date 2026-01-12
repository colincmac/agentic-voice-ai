using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Extensions.AI.RealtimeVoice;

namespace Agents.AI.RealtimeVoice;


public class RealtimeAgentOptions
{

    public RealtimeAgentOptions() { }
    public RealtimeAgentOptions(string? name = null, string? description = null, string? instructions = null)
    {
        Name = name;
        Instructions = instructions;
        Description = description;
    }

    /// <summary>
    /// Gets or sets the agent id.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the agent name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the agent instructions.
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// Gets or sets the agent description.
    /// </summary>
    public string? Description { get; set; }


    public TimeSpan? MaxSessionDuration { get; set; }

    public LiveConversationSessionOptions? SessionOptions { get; set; }
    public Func<ChatMessageStoreFactoryContext, ChatMessageStore>? ChatMessageStoreFactory { get; set; }

    /// <summary>
    /// Gets or sets a factory function to create an instance of <see cref="AIContextProvider"/>
    /// which will be used to create a context provider for each new thread, and can then
    /// provide additional context for each agent run.
    /// </summary>
    public Func<AIContextProviderFactoryContext, AIContextProvider>? AIContextProviderFactory { get; set; }

    public RealtimeAgentOptions Clone()
    {
        return new RealtimeAgentOptions
        {
            Id = Id,
            Name = Name,
            Instructions = Instructions,
            Description = Description,
            ChatMessageStoreFactory = ChatMessageStoreFactory,
            MaxSessionDuration = MaxSessionDuration,
            AIContextProviderFactory = AIContextProviderFactory,
            SessionOptions = SessionOptions?.Clone()
        };
    }

    /// <summary>
    /// Context object passed to the <see cref="AIContextProviderFactory"/> to create a new instance of <see cref="AIContextProvider"/>.
    /// </summary>
    public class AIContextProviderFactoryContext
    {
        /// <summary>
        /// Gets or sets the serialized state of the <see cref="AIContextProvider"/>, if any.
        /// </summary>
        /// <value><see langword="default"/> if there is no state, e.g. when the <see cref="AIContextProvider"/> is first created.</value>
        public JsonElement SerializedState { get; set; }

        /// <summary>
        /// Gets or sets the JSON serialization options to use when deserializing the <see cref="SerializedState"/>.
        /// </summary>
        public JsonSerializerOptions? JsonSerializerOptions { get; set; }
    }

    /// <summary>
    /// Context object passed to the <see cref="ChatMessageStoreFactory"/> to create a new instance of <see cref="ChatMessageStore"/>.
    /// </summary>
    public class ChatMessageStoreFactoryContext
    {
        /// <summary>
        /// Gets or sets the serialized state of the chat message store, if any.
        /// </summary>
        /// <value><see langword="default"/> if there is no state, e.g. when the <see cref="ChatMessageStore"/> is first created.</value>
        public JsonElement SerializedState { get; set; }

        /// <summary>
        /// Gets or sets the JSON serialization options to use when deserializing the <see cref="SerializedState"/>.
        /// </summary>
        public JsonSerializerOptions? JsonSerializerOptions { get; set; }
    }

}
