using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agents.AI.RealtimeVoice.Azure.Monitoring;

/// <summary>
/// Provides comprehensive OpenTelemetry metrics for realtime conversation sessions.
/// Enables monitoring dashboards and alerts for human operators overseeing AI conversations.
/// </summary>
public sealed class ConversationSessionMetrics      
{
    private readonly Meter _meter;
    private readonly ActivitySource _activitySource;

    // Counters
    private readonly Counter<long> _sessionStartedCounter;
    private readonly Counter<long> _sessionCompletedCounter;
    private readonly Counter<long> _sessionFailedCounter;
    private readonly Counter<long> _messageSentCounter;
    private readonly Counter<long> _messageReceivedCounter;
    private readonly Counter<long> _toolInvocationCounter;
    private readonly Counter<long> _authenticationAttemptCounter;
    private readonly Counter<long> _fraudAlertsCounter;

    // Gauges
    private readonly ObservableGauge<int> _activeSessionsGauge;
    private readonly ObservableGauge<int> _activeParticipantsGauge;

    // Histograms
    private readonly Histogram<double> _sessionDurationHistogram;
    private readonly Histogram<double> _messageLatencyHistogram;
    private readonly Histogram<double> _toolExecutionTimeHistogram;
    private readonly Histogram<double> _authenticationTimeHistogram;
    private readonly Histogram<double> _fraudRiskScoreHistogram;
    private readonly Histogram<double> _voiceBiometricConfidenceHistogram;

    private int _activeSessions = 0;
    private int _activeParticipants = 0;

    public ConversationSessionMetrics(string meterName = "Agents.AI.RealtimeVoice.Azure")
    {
        _meter = new Meter(meterName, "1.0.0");
        _activitySource = new ActivitySource(meterName);

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
    }

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

    #region Activity/Tracing

    public Activity? StartSessionActivity(string sessionId, string activityName)
    {
        var activity = _activitySource.StartActivity(activityName);
        activity?.SetTag("session.id", sessionId);
        return activity;
    }

    #endregion

    private TagList CreateTagList(string sessionId, Dictionary<string, object>? additionalTags = null)
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

    public void Dispose()
    {
        _meter?.Dispose();
        _activitySource?.Dispose();
    }
}
