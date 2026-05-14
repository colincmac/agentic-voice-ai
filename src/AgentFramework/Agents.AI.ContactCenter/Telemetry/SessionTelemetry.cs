using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agents.AI.ContactCenter.Media.Analysis;
using Agents.AI.ContactCenter.Configuration;
using static Agents.AI.ContactCenter.Telemetry.ConversationSessionActivitySource;

namespace Agents.AI.ContactCenter.Telemetry;

/// <summary>
/// Centralizes all telemetry (metrics and activities) for the conversation hub and sessions.
/// Injected as a singleton to avoid duplicating Meter/Counter/Histogram boilerplate
/// across Hub and Session classes.
/// </summary>
public sealed class SessionTelemetry : IDisposable
{
    private readonly ActivitySource _activitySource = new(ActivitySourceName);

    private readonly Meter _meter;

    #region Hub Counters
    public Counter<int> SessionsCreated { get; }
    public Counter<int> SessionsClosed { get; }
    public UpDownCounter<int> ActiveSessions { get; }
    public Histogram<double> SessionDuration { get; }
    #endregion

    #region Session Counters
    public UpDownCounter<int> ParticipantsActive { get; }
    public UpDownCounter<int> ChannelsActive { get; }
    public Histogram<double> AudioRoutingLatency { get; }
    public Histogram<double> MessageRoutingLatency { get; }
    private readonly Counter<long> _sessionStartedCounter;
    private readonly Counter<long> _sessionCompletedCounter;
    private readonly Counter<long> _sessionFailedCounter;
    private readonly Counter<long> _messageSentCounter;
    private readonly Counter<long> _messageReceivedCounter;
    private readonly Counter<long> _toolInvocationCounter;
    private readonly Counter<long> _authenticationAttemptCounter;
    private readonly Counter<long> _fraudAlertsCounter;
    #endregion

    #region Analysis

    // Cross-signal analysis metrics
    private readonly Histogram<double> _signalDivergenceHistogram;
    private readonly Histogram<double> _audioAnalysisLatencyHistogram;
    private readonly Histogram<double> _emotionConfidenceHistogram;
    private readonly Counter<long> _signalDivergenceCounter;
    private readonly Histogram<double> _speechStartToResponseHistogram;
    #endregion

    #region Tier Degradation
    private readonly Counter<long> _sessionsCreatedByTierCounter;
    private readonly Counter<long> _tierDegradationsCounter;
    private readonly Counter<long> _midCallFallbacksCounter;
    #endregion

    // Histograms
    private readonly Histogram<double> _sessionDurationHistogram;
    private readonly Histogram<double> _messageLatencyHistogram;
    private readonly Histogram<double> _toolExecutionTimeHistogram;
    private readonly Histogram<double> _authenticationTimeHistogram;
    private readonly Histogram<double> _fraudRiskScoreHistogram;
    private readonly Histogram<double> _voiceBiometricConfidenceHistogram;
    // Gauges
    private readonly ObservableGauge<int> _activeSessionsGauge;
    private readonly ObservableGauge<int> _activeParticipantsGauge;


    private int _activeSessions = 0;
    private int _activeParticipants = 0;


    public SessionTelemetry()
    {
        _meter = new Meter(MeterName);

        SessionsCreated = _meter.CreateCounter<int>(
            HubSessionsCreatedAttributeKey,
            description: "Number of conversation sessions created");

        SessionsClosed = _meter.CreateCounter<int>(
            HubSessionsClosedAttributeKey,
            description: "Number of conversation sessions closed");

        ActiveSessions = _meter.CreateUpDownCounter<int>(
            HubSessionsActiveAttributeKey,
            description: "Number of currently active conversation sessions");

        SessionDuration = _meter.CreateHistogram<double>(
            SessionDurationAttributeKey,
            unit: "s",
            description: "Duration of conversation sessions in seconds");

        ParticipantsActive = _meter.CreateUpDownCounter<int>(SessionParticipantsActiveAttributeKey);
        ChannelsActive = _meter.CreateUpDownCounter<int>(SessionChannelsActiveAttributeKey);

        AudioRoutingLatency = _meter.CreateHistogram<double>(SessionAudioRoutingLatencyAttributeKey, unit: "ms");
        MessageRoutingLatency = _meter.CreateHistogram<double>(SessionMessageRoutingLatencyAttributeKey, unit: "ms");

        // Initialize counters
        _sessionStartedCounter = _meter.CreateCounter<long>(
            "conversation.session.started",
            description: "Number of conversation sessions started");

        _sessionCompletedCounter = _meter.CreateCounter<long>(
            "conversation.session.completed",
            description: "Number of conversation sessions completed successfully");

        _sessionFailedCounter = _meter.CreateCounter<long>(
            "conversation.session.failed",
            description: "Number of conversation sessions that failed");

        _messageSentCounter = _meter.CreateCounter<long>(
            "conversation.message.sent",
            description: "Number of messages sent by the agent");

        _messageReceivedCounter = _meter.CreateCounter<long>(
            "conversation.message.received",
            description: "Number of messages received from participants");

        _toolInvocationCounter = _meter.CreateCounter<long>(
            "conversation.tool.invoked",
            description: "Number of tool invocations");

        _authenticationAttemptCounter = _meter.CreateCounter<long>(
            "conversation.authentication.attempted",
            description: "Number of authentication attempts");

        _fraudAlertsCounter = _meter.CreateCounter<long>(
            "conversation.fraud.alerts",
            description: "Number of fraud alerts triggered");

        // Initialize gauges
        _activeSessionsGauge = _meter.CreateObservableGauge(
            "conversation.session.active",
            () => _activeSessions,
            description: "Number of currently active conversation sessions");

        _activeParticipantsGauge = _meter.CreateObservableGauge(
            "conversation.participants.active",
            () => _activeParticipants,
            description: "Number of currently active participants");

        // Initialize histograms
        _sessionDurationHistogram = _meter.CreateHistogram<double>(
            "conversation.session.duration",
            unit: "ms",
            description: "Duration of conversation sessions");

        _messageLatencyHistogram = _meter.CreateHistogram<double>(
            "conversation.message.latency",
            unit: "ms",
            description: "Latency for message processing");

        _toolExecutionTimeHistogram = _meter.CreateHistogram<double>(
            "conversation.tool.execution_time",
            unit: "ms",
            description: "Tool execution time");

        _authenticationTimeHistogram = _meter.CreateHistogram<double>(
            "conversation.authentication.duration",
            unit: "ms",
            description: "Time taken for authentication");

        _fraudRiskScoreHistogram = _meter.CreateHistogram<double>(
            "conversation.fraud.risk_score",
            description: "Fraud risk score for sessions");

        _voiceBiometricConfidenceHistogram = _meter.CreateHistogram<double>(
            "conversation.voice_biometric.confidence",
            description: "Voice biometric verification confidence");

        // Cross-signal analysis metrics
        _signalDivergenceHistogram = _meter.CreateHistogram<double>(
    "conversation.signal.divergence",
    description: "Divergence score between text sentiment and audio emotion");

        _audioAnalysisLatencyHistogram = _meter.CreateHistogram<double>(
            "conversation.signal.audio_analysis_latency",
            unit: "ms",
            description: "Audio analysis pipeline latency per window");

        _emotionConfidenceHistogram = _meter.CreateHistogram<double>(
            "conversation.signal.emotion_confidence",
            description: "Audio emotion detection confidence");

        _signalDivergenceCounter = _meter.CreateCounter<long>(
            "conversation.signal.divergence_events",
            description: "Count of cross-signal divergence events detected");

        _speechStartToResponseHistogram = _meter.CreateHistogram<double>(
            "conversation.voice.speech_start_to_response",
            unit: "ms",
            description: "Perceived latency from user speech end to agent response start");

        // Tier degradation metrics
        _sessionsCreatedByTierCounter = _meter.CreateCounter<long>(
            "conversation.tier.sessions_created",
            description: "Number of sessions created per agent tier");

        _tierDegradationsCounter = _meter.CreateCounter<long>(
            "conversation.tier.degradations",
            description: "Number of times a session was assigned to a lower tier due to capacity");

        _midCallFallbacksCounter = _meter.CreateCounter<long>(
            "conversation.tier.mid_call_fallbacks",
            description: "Number of mid-call transport swaps to a lower tier");
    }

    public Meter SessionMeter => _meter;

    #region Hub Recording Methods
    public void RecordSessionCreated(string sessionId)
    {
        SessionsCreated.Add(1, new KeyValuePair<string, object?>(SessionIdAttributeKey, sessionId));
        ActiveSessions.Add(1);
    }

    public void RecordSessionClosed(string sessionId, double durationSeconds)
    {
        SessionsClosed.Add(1, new KeyValuePair<string, object?>(SessionIdAttributeKey, sessionId));
        ActiveSessions.Add(-1);
        SessionDuration.Record(durationSeconds, new KeyValuePair<string, object?>(SessionIdAttributeKey, sessionId));
    }
    #endregion

    #region Session Recording Methods
    public void RecordParticipantAdded() => ParticipantsActive.Add(1);
    public void RecordParticipantRemoved() => ParticipantsActive.Add(-1);
    public void RecordChannelAdded() => ChannelsActive.Add(1);
    public void RecordChannelRemoved() => ChannelsActive.Add(-1);

    internal void RecordAudioRouted(string sessionId, string sourceId, int targetCount, int byteCount, double latencyMs, TotalAudioPacketsRoutedCounter packetsCounter, TotalAudioBytesRoutedCounter bytesCounter)
    {
        packetsCounter.Add(targetCount, sessionId, sourceId);
        bytesCounter.Add(byteCount * targetCount, sessionId, sourceId);
        AudioRoutingLatency.Record(latencyMs, new KeyValuePair<string, object?>(SessionTargetChannelCountAttributeKey, targetCount));
    }

    public void RecordMessageRouted(int targetCount, double latencyMs)
    {
        MessageRoutingLatency.Record(latencyMs, new KeyValuePair<string, object?>(SessionTargetChannelCountAttributeKey, targetCount));
    }
    #endregion
    #region Session Metrics

    public void RecordSessionStarted(string sessionId, Dictionary<string, object>? tags = null)
    {
        Interlocked.Increment(ref _activeSessions);
        _sessionStartedCounter.Add(1, CreateTagList(sessionId, tags));
    }

    public void RecordSessionCompleted(string sessionId, double durationMs, Dictionary<string, object>? tags = null)
    {
        Interlocked.Decrement(ref _activeSessions);
        _sessionCompletedCounter.Add(1, CreateTagList(sessionId, tags));
        _sessionDurationHistogram.Record(durationMs, CreateTagList(sessionId, tags));
    }

    public void RecordSessionFailed(string sessionId, string reason, Dictionary<string, object>? tags = null)
    {
        Interlocked.Decrement(ref _activeSessions);
        var tagList = CreateTagList(sessionId, tags);
        tagList.Add(nameof(reason), reason);
        _sessionFailedCounter.Add(1, tagList);
    }
    #endregion

    #region Participant Metrics

    public void RecordParticipantJoined(string sessionId, string participantId, Dictionary<string, object>? tags = null)
    {
        Interlocked.Increment(ref _activeParticipants);
    }

    public void RecordParticipantLeft(string sessionId, string participantId, Dictionary<string, object>? tags = null)
    {
        Interlocked.Decrement(ref _activeParticipants);
    }

    #endregion

    #region Message Metrics

    public void RecordMessageSent(string sessionId, double latencyMs, Dictionary<string, object>? tags = null)
    {
        _messageSentCounter.Add(1, CreateTagList(sessionId, tags));
        _messageLatencyHistogram.Record(latencyMs, CreateTagList(sessionId, tags));
    }

    public void RecordMessageReceived(string sessionId, double latencyMs, Dictionary<string, object>? tags = null)
    {
        _messageReceivedCounter.Add(1, CreateTagList(sessionId, tags));
        _messageLatencyHistogram.Record(latencyMs, CreateTagList(sessionId, tags));
    }

    #endregion

    #region Tool Metrics

    public void RecordToolInvocation(string sessionId, string toolName, double executionTimeMs, bool success, Dictionary<string, object>? tags = null)
    {
        var tagList = CreateTagList(sessionId, tags);
        tagList.Add("tool_name", toolName);
        tagList.Add("success", success);

        _toolInvocationCounter.Add(1, tagList);
        _toolExecutionTimeHistogram.Record(executionTimeMs, tagList);
    }

    #endregion

    #region Authentication Metrics

    public void RecordAuthenticationAttempt(
        string sessionId,
        string method,
        bool success,
        double durationMs,
        Dictionary<string, object>? tags = null)
    {
        var tagList = CreateTagList(sessionId, tags);
        tagList.Add("auth_method", method);
        tagList.Add("success", success);

        _authenticationAttemptCounter.Add(1, tagList);
        _authenticationTimeHistogram.Record(durationMs, tagList);
    }

    #endregion

    #region Fraud Detection Metrics

    public void RecordFraudAlert(string sessionId, string alertType, double riskScore, Dictionary<string, object>? tags = null)
    {
        var tagList = CreateTagList(sessionId, tags);
        tagList.Add("alert_type", alertType);

        _fraudAlertsCounter.Add(1, tagList);
        _fraudRiskScoreHistogram.Record(riskScore, tagList);
    }

    public void RecordFraudRiskScore(string sessionId, double riskScore, Dictionary<string, object>? tags = null)
    {
        _fraudRiskScoreHistogram.Record(riskScore, CreateTagList(sessionId, tags));
    }

    #endregion

    #region Voice Biometric Metrics

    public void RecordVoiceBiometricVerification(
        string sessionId,
        bool success,
        double confidence,
        Dictionary<string, object>? tags = null)
    {
        var tagList = CreateTagList(sessionId, tags);
        tagList.Add("success", success);

        _voiceBiometricConfidenceHistogram.Record(confidence, tagList);
    }

    #endregion

    #region AI Metrics
    #endregion

    #region Tier Degradation Metrics

    public void RecordSessionCreatedAtTier(string sessionId, AgentTier tier)
    {
        var tags = CreateTagList(sessionId);
        tags.Add("agent_tier", tier.ToString());
        _sessionsCreatedByTierCounter.Add(1, tags);

        // If the tier is not the highest priority, it counts as a degradation
        if (tier != AgentTier.RealtimeVoice)
        {
            _tierDegradationsCounter.Add(1, tags);
        }
    }

    public void RecordMidCallFallback(string sessionId, AgentTier fromTier, AgentTier toTier)
    {
        var tags = CreateTagList(sessionId);
        tags.Add("from_tier", fromTier.ToString());
        tags.Add("to_tier", toTier.ToString());
        _midCallFallbacksCounter.Add(1, tags);
    }

    #endregion

    #region ACS Metrics
    #endregion

    #region Activity Helpers
    public Activity? StartHubActivity(string shortOperationName)
    {
        if (!_activitySource.HasListeners())
        {
            return null;
        }

        string activityName = $"{HubActivityAttributeKey} {shortOperationName}";
        return _activitySource.StartActivity(activityName, ActivityKind.Server);
    }

    public Activity? StartSessionActivity(string operationName, string sessionId, Dictionary<string, object>? additionalTags = null)
    {
        if (!_activitySource.HasListeners())
        {
            return null;
        }

        var activity = _activitySource.StartActivity(operationName);
        if (activity is not null)
        {
            activity.SetTag(SessionIdAttributeKey, sessionId);
            if (additionalTags is not null)
            {
                foreach (var tag in additionalTags)
                {
                    activity.SetTag(tag.Key, tag.Value);
                }
            }
        }
        return activity;
    }

    private static TagList CreateTagList(string sessionId, Dictionary<string, object>? additionalTags = null)
    {
        var tagList = new TagList
        {
            { "session.id", sessionId }
        };

        if (additionalTags is not null)
        {
            foreach (var tag in additionalTags)
            {
                tagList.Add(tag.Key, tag.Value);
            }
        }

        return tagList;
    }
    #endregion


    #region Cross-Signal Metrics

    public void RecordSignalAnalysis(
        string sessionId,
        ConversationSignalAnalysis analysis,
        double analysisLatencyMs)
    {
        var tags = CreateTagList(sessionId);

        if (analysis.Divergence.HasValue)
        {
            _signalDivergenceHistogram.Record(analysis.Divergence.Value, tags);
        }

        if (analysis.AudioEmotion?.Confidence is { } confidence)
        {
            _emotionConfidenceHistogram.Record(confidence, tags);
        }

        _audioAnalysisLatencyHistogram.Record(analysisLatencyMs, tags);

        if (analysis.IsDivergent)
        {
            _signalDivergenceCounter.Add(1, tags);
        }
    }

    public void RecordSpeechToResponseLatency(string sessionId, double latencyMs)
    {
        _speechStartToResponseHistogram.Record(latencyMs, CreateTagList(sessionId));
    }
    #endregion

    public void Dispose()
    {
        _meter.Dispose();
    }
}
