using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Extensions.AI.OpenTelemetry.SemanticConventions;

public static partial class GenAI
{

    public static class Calling
    {
        public const string Workflow = "gen_ai.call.workflow_name";
        public const string CallId = "gen_ai.call.call_id";
        public const string TargetUri = "gen_ai.call.call_id";

    }

    public static class Realtime
    {
        /// <summary>
        /// The unique identifier for the real-time session.
        /// </summary>
        public const string RealtimeSessionId = "gen_ai.realtime.session_id";

        /// <summary>
        /// The type of real-time event (e.g., conversation.item.created, response.audio.delta).
        /// </summary>
        public const string RealtimeEventType = "gen_ai.realtime.event_type";

        /// <summary>
        /// The modality of the real-time interaction (e.g., text, audio).
        /// </summary>
        public const string RealtimeOutputModalities = "gen_ai.realtime.output_modalities";

        /// <summary>
        /// The voice used for audio responses (e.g., alloy, echo, shimmer).
        /// </summary>
        public const string RealtimeVoice = "gen_ai.realtime.voice";

        /// <summary>
        /// Indicates whether turn detection is enabled.
        /// </summary>
        public const string RealtimeTurnDetectionEnabled = "gen_ai.realtime.turn_detection.enabled";

        /// <summary>
        /// The type of turn detection (e.g., server_vad).
        /// </summary>
        public const string RealtimeTurnDetectionType = "gen_ai.realtime.turn_detection.type";

        /// <summary>
        /// The threshold value used for turn detection.
        /// </summary>
        public const string RealtimeTurnDetectionThreshold = "gen_ai.realtime.turn_detection.threshold";

        /// <summary>
        /// The silence duration in milliseconds for turn detection.
        /// </summary>
        public const string RealtimeTurnDetectionSilenceDurationMs = "gen_ai.realtime.turn_detection.silence_duration_ms";

        /// <summary>
        /// The audio format (e.g., pcm16, g711_ulaw, g711_alaw).
        /// </summary>
        public const string RealtimeAudioFormat = "gen_ai.realtime.audio.format";

        /// <summary>
        /// The audio sample rate.
        /// </summary>
        public const string RealtimeAudioSampleRate = "gen_ai.realtime.audio.sample_rate";

        /// <summary>
        /// The transcript of the audio content.
        /// </summary>
        public const string RealtimeAudioTranscript = "gen_ai.realtime.audio.transcript";

        /// <summary>
        /// The duration of the audio in milliseconds.
        /// </summary>
        public const string RealtimeAudioDurationMs = "gen_ai.realtime.audio.duration_ms";

        /// <summary>
        /// Time to first byte latency in milliseconds.
        /// </summary>
        public const string RealtimeLatencyFirstByteMs = "gen_ai.realtime.latency.first_byte_ms";

        /// <summary>
        /// The duration of the websocket connection in milliseconds.
        /// </summary>
        public const string RealtimeConnectionDurationMs = "gen_ai.realtime.connection.duration_ms";

        /// <summary>
        /// The number of items in the conversation.
        /// </summary>
        public const string RealtimeConversationItemsCount = "gen_ai.realtime.conversation.items.count";
    }
}
