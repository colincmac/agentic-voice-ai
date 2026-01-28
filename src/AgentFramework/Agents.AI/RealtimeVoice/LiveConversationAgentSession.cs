using System.Text.Json;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
namespace Agents.AI.RealtimeVoice;

public sealed class LiveConversationAgentSession : TranscriptTrackingAgentThread, IDisposable
{
    public ILiveConversationSession Session { get; set; }
    public readonly SemaphoreSlim _sessionGate = new(1);

    public string? ActiveSessionId => Session.SessionId;

    public LiveConversationAgentSession(ILiveConversationSession session)
    {
        Session = session;
    }
    internal LiveConversationAgentSession(
        ILiveConversationSession session,
    JsonElement serializedThreadState,
    JsonSerializerOptions? jsonSerializerOptions = null,
    Func<JsonElement, JsonSerializerOptions?, ChatMessageStore>? chatMessageStoreFactory = null,
    Func<JsonElement, JsonSerializerOptions?, AIContextProvider>? aiContextProviderFactory = null) : base(serializedThreadState,jsonSerializerOptions,chatMessageStoreFactory,aiContextProviderFactory)
    {
        Session = session;
    }


    // Cleanup methods
    public void Dispose()
    {
        _sessionGate.Dispose();
        Session.Dispose();
    }
}
