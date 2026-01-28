using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.LiveVoice;

public class RealtimeVoiceAgentTurn(List<ChatMessage>? turnMessages = null)
{
    public DateTimeOffset TurnStartTime { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? TurnEndTime { get; set; }

    public TimeSpan? TurnDuration => TurnEndTime.HasValue ? TurnEndTime - TurnStartTime : null;

    public IList<ChatMessage> TranscriptionMessages { get; set; } = turnMessages ?? [];
}

public sealed class RealtimeConversationUtterance(ChatMessage message)
{
    public DateTimeOffset UtteranceStartTime { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UtteranceEndTime { get; set; }
     
    public ChatMessage Message { get; set; } = message;

    public TimeSpan? TurnDuration => UtteranceEndTime.HasValue ? UtteranceEndTime - UtteranceStartTime : null;
}
