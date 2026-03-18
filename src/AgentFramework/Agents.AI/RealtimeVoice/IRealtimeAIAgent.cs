using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.RealtimeVoice;

public interface IRealtimeAIAgent
{
    string Id { get; }
    string DisplayName { get; }
    Task<AgentResponse?> CancelRunAsync(string id, AgentRunOptions? options = null, CancellationToken cancellationToken = default);
    Task<AgentResponse?> DeleteRunAsync(string id, AgentRunOptions? options = null, CancellationToken cancellationToken = default);
    Task<LiveConversationAgentSession> GetNewSessionAsync(CancellationToken cancellationToken = default);
    Task SendAudioToRunAsync(DataContent audio, AgentSession session, CancellationToken cancellationToken = default);
    Task SendMessagesToRunAsync(IEnumerable<ChatMessage> messages, AgentSession session, CancellationToken cancellationToken = default);
}
