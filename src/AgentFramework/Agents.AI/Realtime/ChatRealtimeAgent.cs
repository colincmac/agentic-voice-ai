using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.AI;

namespace Agents.AI.Realtime;

public class ChatRealtimeAgent : IRealtimeAgent
{
    public ValueTask<RealtimeAIAgentSession> CreateRealtimeSessionAsync(RealtimeSessionOptions? sessionOptions = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task SendAsync(RealtimeAIAgentSession session, RealtimeClientMessage message, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
