using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.RealtimeVoice;

public static class AIAgentRealtimeExtensions
{
    public static Task SendAudioToRunAsync(this AIAgent agent,
        DataContent audio,
        AgentThread thread,
        CancellationToken cancellationToken = default)
    {

        var realtimeAgent = agent.GetService<RealtimeAIAgent>() ?? throw new InvalidOperationException($"Tried to invoke AIAgent type {typeof(RealtimeAIAgent)}, but it was not found in the AIAgent services");
        return realtimeAgent.SendAudioToRunAsync(audio, thread, cancellationToken);
    }

    public static Task SendMessagesToRunAsync(
        this AIAgent agent,
        IEnumerable<ChatMessage> messages,
        AgentThread thread,
        CancellationToken cancellationToken = default)
    {
        var realtimeAgent = agent.GetService<RealtimeAIAgent>() ?? throw new InvalidOperationException($"Tried to invoke AIAgent type {typeof(RealtimeAIAgent)}, but it was not found in the AIAgent services");
        return realtimeAgent.SendMessagesToRunAsync(messages, thread, cancellationToken);
    }

}
