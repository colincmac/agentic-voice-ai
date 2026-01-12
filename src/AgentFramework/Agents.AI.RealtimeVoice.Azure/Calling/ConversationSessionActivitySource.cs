using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.Metrics;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

internal static partial class ConversationSessionActivitySource
{
    public const string ActivitySourceName = "Agents.AI.RealtimeVoice.ContactCenter";
    public const string MeterName = "Agents.AI.RealtimeVoice.ContactCenter";

    #region Hub Attributes
    public const string HubActivityAttributeKey = "conversation_hub";
    public const string HubSessionDurationAttributeKey = "conversation_hub.session_duration_seconds";
    public const string HubSessionTotalActiveAttributeKey = "conversation_hub.session_total_active";
    public const string HubSessionIsNewAttributeKey = "conversation_hub.session_is_new";
    public const string HubSessionAbandonedAttributeKey = "conversation_hub.session_abandoned_count";
    public const string HubSessionCutoffTimeAttributeKey = "conversation_hub.session_cutoff_time";
    public const string HubOperationAttributeKey = "conversation_hub.operation";

    public const string HubSessionsCreatedAttributeKey = "conversation_hub.sessions_created";
    public const string HubSessionsClosedAttributeKey = "conversation_hub.sessions_closed";
    public const string HubSessionsActiveAttributeKey = "conversation_hub.sessions_active";

    public static class HubOperations
    {
        public const string StopHub = "stop_hub";
        public const string StartHub = "start_hub";
        public const string CreateSession = "create_session";
        public const string RemoveSession = "remove_session";
        public const string CleanupAbandonedSessions = "cleanup_abandoned_sessions";
    }
    #endregion

        #region Session Attributes
    public const string SessionActivityAttributeKey = "conversation_hub.session";
    public const string SessionIdAttributeKey = "conversation_hub.session.id";
    public const string SessionDurationAttributeKey = "conversation_hub.session.duration";

    public const string SessionChannelIdAttributeKey = "conversation_hub.session.channel.id";
    public const string SessionChannelTypeAttributeKey = "conversation_hub.session.channel.type";
    public const string SessionChannelsAddedAttributeKey = "conversation_hub.session.channels_added";
    public const string SessionChannelsActiveAttributeKey = "conversation_hub.session.channels_active";
    public const string SessionSourceChannelAttributeKey = "conversation_hub.session.source_channel.id";
    public const string SessionTargetChannelAttributeKey = "conversation_hub.session.target_channel.id";
    public const string SessionTargetChannelCountAttributeKey = "conversation_hub.session.target_channel_count";

    public const string SessionParticipantIdAttributeKey = "conversation_hub.session.participant.id";
    public const string SessionParticipantsActiveAttributeKey = "conversation_hub.session.participants_active";

    public const string SessionAudioBytesRoutedAttributeKey = "conversation_hub.session.audio.bytes_routed";
    public const string SessionAudioPacketsRoutedAttributeKey = "conversation_hub.session.audio.packets_routed";
    public const string SessionAudioRoutingLatencyAttributeKey = "conversation_hub.session.audio.routing_latency";
    public const string SessionMessageRoutingLatencyAttributeKey = "conversation_hub.session.message.routing_latency";
    #endregion

    public static ActivitySource ActivitySource { get; } = new ActivitySource(ActivitySourceName);

    [Counter<long>(SessionIdAttributeKey, SessionSourceChannelAttributeKey, Name = SessionAudioBytesRoutedAttributeKey), Description("Total audio bytes routed between channels")]
    public static partial TotalAudioBytesRoutedCounter CreateAudioBytesBetweenChannels(Meter meter);

    [Counter<long>(SessionIdAttributeKey, SessionSourceChannelAttributeKey, Name = SessionAudioPacketsRoutedAttributeKey), Description("Total audio packets routed between channels")]
    public static partial TotalAudioPacketsRoutedCounter CreateAudioPacketsBetweenChannels(Meter meter);

    [Histogram<double>(SessionAudioRoutingLatencyAttributeKey), Description("Audio routing latency")]
    public static partial AudioRoutingLatency CreateAudioRoutingLatency(Meter meter);

    [Histogram<double>(SessionMessageRoutingLatencyAttributeKey), Description("Message routing latency")]
    public static partial MessageRoutingLatency CreateMessageRoutingLatency(Meter meter);

    private static void SetActivityError(Activity? activity, Exception ex)
    {
        activity?.SetTag("error.type", ex.GetType().FullName);
        activity?.SetStatus(ActivityStatusCode.Error);
    }
}
