using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.Metrics;

namespace Agents.AI.RealtimeVoice.Azure.Monitoring;

internal static partial class ConversationSessionActivitySource
{
    public const string ActivitySourceName = "Agents.AI.ContactCenter";
    public const string HubMeterName = "Agents.AI.ContactCenter.Hub";
    public const string MeterName = "Agents.AI.ContactCenter";

    #region Hub Attributes
    public const string HubActivityAttributeKey = "conversation";
    public const string HubSessionDurationAttributeKey = "conversation.session_duration_seconds";
    public const string HubSessionTotalActiveAttributeKey = "conversation.session_total_active";
    public const string HubSessionIsNewAttributeKey = "conversation.session_is_new";
    public const string HubSessionAbandonedAttributeKey = "conversation.session_abandoned_count";
    public const string HubSessionCutoffTimeAttributeKey = "conversation.session_cutoff_time";
    public const string HubOperationAttributeKey = "conversation.operation";

    public const string HubSessionsCreatedAttributeKey = "conversation.sessions_created";
    public const string HubSessionsClosedAttributeKey = "conversation.sessions_closed";
    public const string HubSessionsActiveAttributeKey = "conversation.sessions_active";

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
    public const string SessionActivityAttributeKey = "conversation.session";
    public const string SessionIdAttributeKey = "conversation.session.id";
    public const string SessionDurationAttributeKey = "conversation.session.duration";

    public const string SessionChannelIdAttributeKey = "conversation.session.channel.id";
    public const string SessionChannelTypeAttributeKey = "conversation.session.channel.type";
    public const string SessionChannelsAddedAttributeKey = "conversation.session.channels_added";
    public const string SessionChannelsActiveAttributeKey = "conversation.session.channels_active";
    public const string SessionSourceChannelAttributeKey = "conversation.session.source_channel.id";
    public const string SessionTargetChannelAttributeKey = "conversation.session.target_channel.id";
    public const string SessionTargetChannelCountAttributeKey = "conversation.session.target_channel_count";

    public const string SessionParticipantIdAttributeKey = "conversation.session.participant.id";
    public const string SessionParticipantsActiveAttributeKey = "conversation.session.participants_active";

    public const string SessionAudioBytesRoutedAttributeKey = "conversation.session.audio.bytes_routed";
    public const string SessionAudioPacketsRoutedAttributeKey = "conversation.session.audio.packets_routed";
    public const string SessionAudioRoutingLatencyAttributeKey = "conversation.session.audio.routing_latency";
    public const string SessionMessageRoutingLatencyAttributeKey = "conversation.session.message.routing_latency";
    #endregion


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
