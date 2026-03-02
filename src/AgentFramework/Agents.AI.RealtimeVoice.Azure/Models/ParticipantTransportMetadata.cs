namespace Agents.AI.RealtimeVoice.Azure.Models;


public enum ParticipantType
{
    Customer,
    Agent,
    AIAgent,
    Supervisor,
    System
}
public enum ParticipantRole
{
    Caller,
    Callee,
    Observer,
    Assistant,
    Moderator
}

/// <summary>
/// Represents the type of communication channel for a participant contact.
/// </summary>
public enum CommunicationChannelType
{
    /// <summary>Teams user channel</summary>
    TeamsChatThread,
    
    /// <summary>ACS phone call channel</summary>
    Phone,
    
    /// <summary>Chat AI agent channel</summary>
    ChatAIAgent,
    
    /// <summary>Voice AI agent channel</summary>
    VoiceAIAgent,

    /// <summary>ACS user channel</summary>
    AcsUser,

    /// <summary>A2A (Agent-to-Agent) cross-agent communication channel</summary>
    A2AAgent,

    Unknown
}

public enum ContactStatus
{
    Connecting,
    Connected,
    OnHold,
    Transferring,
    Disconnected,
    Failed
}

/// <summary>
/// Represents a single communication channel for a participant in the conversation hub.
/// A participant may have multiple contacts representing different channels they're communicating through.
/// </summary>
public class ParticipantTransportMetadata
{
    /// <summary>
    /// Unique identifier for this contact.
    /// </summary>
    public required string ContactId { get; init; }
    
    /// <summary>
    /// The type of communication channel.
    /// </summary>
    public required CommunicationChannelType ChannelType { get; init; }
    
    /// <summary>
    /// Raw identifier from the underlying service (e.g., Teams user ID, phone number, etc.).
    /// </summary>
    public required string RawIdentifier { get; init; }
    
    /// <summary>
    /// Display name for this contact.
    /// </summary>
    public string? DisplayName { get; set; }
    
    /// <summary>
    /// Call connection ID if this contact is part of an ACS call.
    /// </summary>
    public string? CallConnectionId { get; set; }
    
    /// <summary>
    /// Server call ID if this contact is part of an ACS call.
    /// </summary>
    public string? ServerCallId { get; set; }
    
    /// <summary>
    /// Indicates whether this contact supports audio communication.
    /// </summary>
    public bool SupportsAudio { get; init; }
    
    /// <summary>
    /// Indicates whether this contact supports message/chat communication.
    /// </summary>
    public bool SupportsMessaging { get; init; }

    public bool SupportsVideo { get; init; }
    public bool SupportsScreenShare { get; init; }

    /// <summary>
    /// The role this channel plays in the session routing topology.
    /// Determines what content gets routed to/from this channel.
    /// Flags are combinable — a channel can serve multiple roles simultaneously.
    /// </summary>
    public ChannelRole Role { get; init; } = ChannelRole.None;

    /// <summary>
    /// Whether this contact is currently muted.
    /// </summary>
    public bool IsMuted { get; set; }
    
    /// <summary>
    /// Whether this contact is currently on hold.
    /// </summary>
    public bool IsOnHold { get; set; }
    
    /// <summary>
    /// Timestamp when this contact joined the conversation.
    /// </summary>
    public DateTimeOffset JoinedAt { get; init; } = DateTimeOffset.UtcNow;
    
    /// <summary>
    /// Additional metadata for this contact.
    /// </summary>
    public Dictionary<string, object?> Metadata { get; init; } = new();
}
