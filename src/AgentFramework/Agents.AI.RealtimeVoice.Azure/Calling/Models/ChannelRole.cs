namespace Agents.AI.RealtimeVoice.Azure.Calling.Models;

/// <summary>
/// Declares the role a channel plays within a session, enabling
/// the session router to make targeted routing decisions.
/// A single channel can serve multiple roles (flags are combinable).
/// </summary>
[Flags]
public enum ChannelRole
{
    /// <summary>No specific role assigned.</summary>
    None = 0,

    /// <summary>
    /// Primary real-time voice channel (PSTN, VOIP, Realtime AI).
    /// Audio is routed directly between primary voice channels with zero added latency.
    /// </summary>
    PrimaryVoice = 1 << 0,

    /// <summary>
    /// Interactive messaging channel (Teams chat, web chat, SignalR).
    /// Receives transcripts and can send messages into the session.
    /// </summary>
    InteractiveMessaging = 1 << 1,

    /// <summary>
    /// Data/media channel (screen share, document upload, video).
    /// Produces continuous data streams consumed by modality processors.
    /// </summary>
    DataStream = 1 << 2,

    /// <summary>
    /// System integration channel (Dynamics CRM, ticketing, knowledge base).
    /// Publishes structured data into the context bus.
    /// </summary>
    SystemIntegration = 1 << 3,

    /// <summary>
    /// Control plane only (operator dashboard, approval interface).
    /// Receives context events and sends control commands.
    /// </summary>
    ControlPlane = 1 << 4,

    /// <summary>
    /// Context consumer — subscribes to the <see cref="SessionContextBus"/>
    /// and injects aggregated context into an AI agent's reasoning.
    /// </summary>
    ContextSink = 1 << 5
}
