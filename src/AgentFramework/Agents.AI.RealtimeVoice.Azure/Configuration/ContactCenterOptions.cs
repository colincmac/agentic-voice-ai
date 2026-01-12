using System.Text.Json.Serialization;

namespace Agents.AI.RealtimeVoice.Azure.Configuration;

public class ContactCenterOptions
{
    public const string SectionName = "ContactCenter";

    [JsonIgnore]
    public AgentFunctionInvocationMiddleware? AgentFunctionInvocationMiddleware { get; set; } = null;
    public string? RealtimeAgentServiceKey { get; set; }

    /// <summary>
    /// Enable Entra identity verification
    /// </summary>
    public bool EnableEntraVerification { get; set; } = true;

    /// <summary>
    /// Enable fraud detection monitoring
    /// </summary>
    public bool EnableFraudDetection { get; set; } = true;

    /// <summary>
    /// Enable voice biometric verification
    /// </summary>
    public bool EnableVoiceBiometrics { get; set; } = true;

    /// <summary>
    /// Enable background agent orchestration
    /// </summary>
    public bool EnableBackgroundAgents { get; set; } = true;

    /// <summary>
    /// Enable session-scoped tools
    /// </summary>
    public bool EnableSessionTools { get; set; } = true;

    /// <summary>
    /// Enable voice approval middleware
    /// </summary>
    public bool EnableVoiceApproval { get; set; } = true;

    /// <summary>
    /// Enable OpenTelemetry metrics
    /// </summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    /// Fraud detection options
    /// </summary>
    public FraudDetectionOptions? FraudDetection { get; set; }
    /// <summary>
    /// Voice biometric options
    /// </summary>
    public VoiceBiometricOptions? VoiceBiometrics { get; set; }
}
public sealed class FraudDetectionOptions
{
    public double HighRiskThreshold { get; set; } = 50.0;
    public double CriticalRiskThreshold { get; set; } = 75.0;
    public bool EnableRealTimeAlerts { get; set; } = true;
    public TimeSpan RapidRequestWindow { get; set; } = TimeSpan.FromSeconds(1);
}
public sealed class VoiceBiometricOptions
{
    public int MinimumEnrollmentSamples { get; set; } = 3;
    public double VerificationThreshold { get; set; } = 0.85; // 85% confidence
    public double AnomalyThreshold { get; set; } = 0.6; // 60% anomaly score
    public bool EnableLivenessDetection { get; set; } = true;
    public bool EnableSyntheticVoiceDetection { get; set; } = true;
}
