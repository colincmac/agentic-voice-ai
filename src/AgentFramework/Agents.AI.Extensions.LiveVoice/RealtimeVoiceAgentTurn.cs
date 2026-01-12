using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.LiveVoice;

public class RealtimeVoiceAgentTurn(List<ChatMessage>? turnMessages = null) //: AIContent
{
    public DateTimeOffset TurnStartTime { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? TurnEndTime { get; set; }
    public IList<ChatMessage> TranscriptionMessages { get; set; } = turnMessages ?? [];
}
