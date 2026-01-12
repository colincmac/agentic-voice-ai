using System;
using System.Collections.Generic;
using System.Text;
using Agents.AI.RealtimeVoice;
using Extensions.AI.RealtimeVoice;

namespace Agents.AI.Extensions.RealtimeAgentHelpers;

public interface IUpdateableRealtimeAgent
{
    Task ConfigureSessionAsync(
    LiveConversationSessionOptions options,
    ConversationSessionThread thread,
    CancellationToken cancellationToken = default);
}
