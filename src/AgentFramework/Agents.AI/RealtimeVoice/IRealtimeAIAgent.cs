using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.RealtimeVoice;

public interface IRealtimeAIAgent
{
    string Id { get; }
    string DisplayName { get; }
    Task<AgentRunResponse?> CancelRunAsync(string id, AgentRunOptions? options = null, CancellationToken cancellationToken = default);
    Task<AgentRunResponse?> DeleteRunAsync(string id, AgentRunOptions? options = null, CancellationToken cancellationToken = default);
    Task<ConversationSessionThread> GetNewThreadAsync(CancellationToken cancellationToken = default);
    Task SendAudioToRunAsync(DataContent audio, AgentThread thread, CancellationToken cancellationToken = default);
    Task SendMessagesToRunAsync(IEnumerable<ChatMessage> messages, AgentThread thread, CancellationToken cancellationToken = default);
}
