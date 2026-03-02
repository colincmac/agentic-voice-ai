namespace Agents.AI.RealtimeVoice.Azure.Models;

/// <summary>
/// Represents a participant in a live call for operator dashboard monitoring.
/// </summary>
public sealed class LiveParticipantSummary
{
    /// <summary>
    /// Unique identifier for the participant within the session.
    /// </summary>
    public required string ParticipantId { get; init; }

    /// <summary>
    /// Channel identifier for the participant within the session (e.g. phone number, chat thread, etc.).
    /// </summary>
    public string? ParticipantChannelIdentifier { get; set; }

    /// <summary>
    /// Display name shown to operators (e.g., customer name, agent name, or phone number).
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Type of participant (Customer, Agent, AIAgent, etc.).
    /// </summary>
    public ParticipantType ParticipantType { get; set; } = ParticipantType.Customer;

    /// <summary>
    /// Role of the participant in the call (Caller, Callee, Observer, etc.).
    /// </summary>
    public ParticipantRole Role { get; set; } = ParticipantRole.Caller;

    /// <summary>
    /// Primary communication channel type for this participant.
    /// </summary>
    public CommunicationChannelType ChannelType { get; set; } = CommunicationChannelType.Phone;

    /// <summary>
    /// When this participant joined the call.
    /// </summary>
    public DateTimeOffset JoinedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Whether the participant is currently muted.
    /// </summary>
    public bool IsMuted { get; set; }

    /// <summary>
    /// Whether the participant is on hold.
    /// </summary>
    public bool IsOnHold { get; set; }

    /// <summary>
    /// Whether the participant is still connected.
    /// </summary>
    public bool IsConnected { get; set; } = true;

    /// <summary>
    /// Creates a deep copy of this participant summary.
    /// </summary>
    public LiveParticipantSummary Clone()
    {
        return new LiveParticipantSummary
        {
            ParticipantId = ParticipantId,
            DisplayName = DisplayName,
            ParticipantType = ParticipantType,
            Role = Role,
            ChannelType = ChannelType,
            JoinedAt = JoinedAt,
            IsMuted = IsMuted,
            IsOnHold = IsOnHold,
            IsConnected = IsConnected
        };
    }

    /// <summary>
    /// Creates a <see cref="LiveParticipantSummary"/> from transport metadata and participant context.
    /// </summary>
    public static LiveParticipantSummary FromMetadata(
        string participantId,
        ParticipantTransportMetadata metadata,
        ParticipantType? participantType = null)
    {
        return new LiveParticipantSummary
        {
            ParticipantId = participantId,
            DisplayName = metadata.DisplayName,
            ParticipantType = participantType ?? InferParticipantType(metadata.ChannelType),
            ChannelType = metadata.ChannelType,
            JoinedAt = metadata.JoinedAt,
            IsMuted = metadata.IsMuted,
            IsOnHold = metadata.IsOnHold,
            IsConnected = true
        };
    }

    private static ParticipantType InferParticipantType(CommunicationChannelType channelType)
    {
        return channelType switch
        {
            CommunicationChannelType.Phone => ParticipantType.Customer,
            CommunicationChannelType.ChatAIAgent => ParticipantType.AIAgent,
            CommunicationChannelType.VoiceAIAgent => ParticipantType.AIAgent,
            CommunicationChannelType.TeamsChatThread => ParticipantType.Agent,
            CommunicationChannelType.AcsUser => ParticipantType.Agent,
            _ => ParticipantType.Customer
        };
    }
}
