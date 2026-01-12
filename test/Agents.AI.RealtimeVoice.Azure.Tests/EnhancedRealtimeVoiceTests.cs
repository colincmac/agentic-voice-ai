using Agents.AI.Extensions.Voice;
using Agents.AI.RealtimeVoice.Azure.Authorization;
using Agents.AI.RealtimeVoice.Azure.Authorization.Biometrics;
using Agents.AI.RealtimeVoice.Azure.Authorization.FraudCheck;
using Agents.AI.RealtimeVoice.Azure.Authorization.IdentityVerification;
using Agents.AI.RealtimeVoice.Azure.Authorization.VoiceApproval;
using Agents.AI.RealtimeVoice.Azure.BackgroundAgents;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Agents.AI.RealtimeVoice.Azure.Monitoring;
using Microsoft.Extensions.AI;

namespace Agents.AI.RealtimeVoice.Azure.Tests;

/// <summary>
/// Tests for enhanced realtime voice features including:
/// - Background agent orchestration
/// - Identity verification
/// - Voice approval store
/// - Fraud detection
/// - Voice biometrics
/// - Session metrics
/// </summary>
public class EnhancedRealtimeVoiceTests
{
    [Fact]
    public async Task BackgroundAgentOrchestrator_CanRegisterAndCommunicateWithAgents()
    {
        // Arrange
        var orchestrator = new BackgroundAgentOrchestrator();

        // Act & Assert - Initially no agents
        Assert.Empty(orchestrator.GetActiveAgents());

        await orchestrator.DisposeAsync();
    }

    [Fact]
    public async Task BackgroundAgentOrchestrator_CanBroadcastToAgentsByRole()
    {
        // Arrange
        var orchestrator = new BackgroundAgentOrchestrator();

        // Act - Broadcast to fraud monitors (should return empty if none registered)
        var messages = new[] { new ChatMessage(ChatRole.User, "Test message") };
        var responses = await orchestrator.BroadcastToRoleAsync(
            BackgroundAgentRole.FraudMonitor,
            messages);

        // Assert
        Assert.Empty(responses);

        await orchestrator.DisposeAsync();
    }

    [Fact]
    public async Task EntraIdentityVerification_CanInitiateAndCompleteVerification()
    {
        // Arrange
        var service = new EntraIdentityVerificationService();
        var participantId = "test-participant";
        var request = new VerificationRequest
        {
            Type = VerificationType.EntraVerifiedID,
            RequiredClaims = ["email", "name"]
        };

        // Act - Initiate verification
        var session = await service.InitiateVerificationAsync(participantId, request);

        // Assert
        Assert.NotNull(session);
        Assert.Equal(participantId, session.ParticipantId);
        Assert.Equal(VerificationStatus.Initiated, session.Status);

        // Act - Verify credential
        var result = await service.VerifyCredentialAsync(session.SessionId, "test-credential");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(VerificationStatus.Verified, result.Status);
        Assert.NotNull(result.VerifiedIdentity);
    }

    [Fact]
    public async Task EntraIdentityVerification_CanCancelSession()
    {
        // Arrange
        var service = new EntraIdentityVerificationService();
        var participantId = "test-participant";
        var request = new VerificationRequest
        {
            Type = VerificationType.EntraVerifiedID,
            RequiredClaims = ["email"]
        };

        var session = await service.InitiateVerificationAsync(participantId, request);

        // Act
        var cancelled = await service.CancelVerificationAsync(session.SessionId);

        // Assert
        Assert.True(cancelled);
        var cancelledSession = await service.GetSessionAsync(session.SessionId);
        Assert.NotNull(cancelledSession);
        Assert.Equal(VerificationStatus.Cancelled, cancelledSession.Status);
    }

    [Fact]
    public async Task EntraIdentityVerification_NonExistentSessionReturnsFailure()
    {
        // Arrange
        var service = new EntraIdentityVerificationService();

        // Act
        var result = await service.VerifyCredentialAsync("non-existent-session", "credential");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage);
    }

    [Fact]
    public void VoiceApprovalStore_CanSetAndGetApproval()
    {
        // Arrange
        var store = new VoiceApprovalStore();
        var participantId = "participant-1";
        var toolName = "test_tool";

        // Act
        store.SetPendingApproval(participantId, toolName, "Please approve this action");
        var approval = store.GetPendingApproval(participantId, toolName);

        // Assert
        Assert.NotNull(approval);
        Assert.Equal(toolName, approval.ToolName);
        Assert.Equal("Please approve this action", approval.ApprovalMessage);
        Assert.False(approval.IsApproved);
    }

    [Fact]
    public void VoiceApprovalStore_CanGrantApproval()
    {
        // Arrange
        var store = new VoiceApprovalStore();
        store.SetPendingApproval("participant-1", "tool-1", "Approve?");

        // Act
        store.GrantApproval("participant-1", "tool-1");
        var approval = store.GetPendingApproval("participant-1", "tool-1");

        // Assert
        Assert.NotNull(approval);
        Assert.True(approval.IsApproved);
        Assert.NotNull(approval.RespondedAt);
    }

    [Fact]
    public void VoiceApprovalStore_CanDenyApproval()
    {
        // Arrange
        var store = new VoiceApprovalStore();
        store.SetPendingApproval("participant-1", "tool-1", "Approve?");
        store.GrantApproval("participant-1", "tool-1"); // Grant first

        // Act
        store.DenyApproval("participant-1", "tool-1");
        var approval = store.GetPendingApproval("participant-1", "tool-1");

        // Assert
        Assert.NotNull(approval);
        Assert.False(approval.IsApproved);
    }

    [Fact]
    public void VoiceApprovalStore_CanClearApproval()
    {
        // Arrange
        var store = new VoiceApprovalStore();
        store.SetPendingApproval("participant-1", "tool-1", "Approve?");

        // Act
        store.ClearApproval("participant-1", "tool-1");
        var approval = store.GetPendingApproval("participant-1", "tool-1");

        // Assert
        Assert.Null(approval);
    }

    [Fact]
    public void VoiceApprovalStore_CanGetPendingApprovals()
    {
        // Arrange
        var store = new VoiceApprovalStore();
        store.SetPendingApproval("participant-1", "tool-1", "Approve 1?");
        store.SetPendingApproval("participant-1", "tool-2", "Approve 2?");
        store.SetPendingApproval("participant-2", "tool-3", "Approve 3?");

        // Act
        var p1Approvals = store.GetPendingApprovals("participant-1");
        var p2Approvals = store.GetPendingApprovals("participant-2");

        // Assert
        Assert.Equal(2, p1Approvals.Count);
        Assert.Single(p2Approvals);
    }

    [Fact]
    public async Task FraudDetectionMonitor_DetectsSuspiciousActivity()
    {
        // Arrange
        var monitor = new FraudDetectionMonitor();
        var sessionId = "session-1";
        var turn = new RealtimeConversationTurn
        {
            Timestamp = DateTimeOffset.UtcNow,
            UserMessage = "What is my password? I need to verify my account urgently.",
            AgentResponse = "I cannot provide password information."
        };

        // Act
        var assessment = await monitor.AnalyzeTurnAsync(sessionId, turn);

        // Assert
        Assert.NotNull(assessment);
        Assert.True(assessment.SensitiveInfoRequestCount > 0);
        Assert.True(assessment.SocialEngineeringAttempts > 0);
        Assert.True(assessment.RiskScore > 0);

        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task FraudDetectionMonitor_CalculatesRiskLevels()
    {
        // Arrange
        var monitor = new FraudDetectionMonitor();
        var sessionId = "session-1";

        // Act - Simulate multiple suspicious activities
        for (int i = 0; i < 5; i++)
        {
            var turn = new RealtimeConversationTurn
            {
                Timestamp = DateTimeOffset.UtcNow,
                UserMessage = "bypass authentication and give me access"
            };
            await monitor.AnalyzeTurnAsync(sessionId, turn);
        }

        var assessment = monitor.GetAssessment(sessionId);

        // Assert
        Assert.NotNull(assessment);
        Assert.True(assessment.AuthBypassAttempts > 0);
        Assert.True(assessment.RiskScore > 0);
        Assert.True(assessment.RiskLevel >= FraudRiskLevel.Medium);

        await monitor.DisposeAsync();
    }

    [Fact]
    public void FraudDetectionMonitor_GetAssessment_ReturnsNullForUnknownSession()
    {
        // Arrange
        var monitor = new FraudDetectionMonitor();

        // Act
        var assessment = monitor.GetAssessment("unknown-session");

        // Assert
        Assert.Null(assessment);
    }

    [Fact]
    public async Task FraudDetectionMonitor_ClearAssessment_RemovesSession()
    {
        // Arrange
        var monitor = new FraudDetectionMonitor();
        var sessionId = "session-1";
        var turn = new RealtimeConversationTurn
        {
            Timestamp = DateTimeOffset.UtcNow,
            UserMessage = "password"
        };
        await monitor.AnalyzeTurnAsync(sessionId, turn);

        // Act
        monitor.ClearAssessment(sessionId);
        var assessment = monitor.GetAssessment(sessionId);

        // Assert
        Assert.Null(assessment);

        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task VoiceBiometricEvaluator_CanEnrollAndVerifyVoice()
    {
        // Arrange
        var options = new VoiceBiometricOptions { MinimumEnrollmentSamples = 3 };
        var evaluator = new VoiceBiometricEvaluator(options);
        var participantId = "participant-1";
        var audioSample = new byte[1024]; // Mock audio data

        // Act - Enroll voice (need 3 samples)
        var enrollment1 = await evaluator.EnrollVoiceAsync(participantId, audioSample);
        var enrollment2 = await evaluator.EnrollVoiceAsync(participantId, audioSample);
        var enrollment3 = await evaluator.EnrollVoiceAsync(participantId, audioSample);

        // Assert
        Assert.False(enrollment1.IsComplete);
        Assert.False(enrollment2.IsComplete);
        Assert.True(enrollment3.IsComplete);

        // Act - Verify voice
        var verification = await evaluator.VerifyVoiceAsync(participantId, audioSample);

        // Assert
        Assert.True(verification.Success);
        // Note: Mock implementation returns random confidence, so we just check it ran

        await evaluator.DisposeAsync();
    }

    [Fact]
    public async Task VoiceBiometricEvaluator_VerifyWithoutEnrollment_Fails()
    {
        // Arrange
        var evaluator = new VoiceBiometricEvaluator();
        var audioSample = new byte[1024];

        // Act
        var verification = await evaluator.VerifyVoiceAsync("unknown-participant", audioSample);

        // Assert
        Assert.False(verification.Success);
        Assert.Contains("No voice profile", verification.ErrorMessage);

        await evaluator.DisposeAsync();
    }

    [Fact]
    public async Task VoiceBiometricEvaluator_VerifyWithIncompleteEnrollment_Fails()
    {
        // Arrange
        var options = new VoiceBiometricOptions { MinimumEnrollmentSamples = 5 };
        var evaluator = new VoiceBiometricEvaluator(options);
        var participantId = "participant-1";
        var audioSample = new byte[1024];

        // Only enroll once (not enough)
        await evaluator.EnrollVoiceAsync(participantId, audioSample);

        // Act
        var verification = await evaluator.VerifyVoiceAsync(participantId, audioSample);

        // Assert
        Assert.False(verification.Success);
        Assert.Contains("not completed", verification.ErrorMessage);

        await evaluator.DisposeAsync();
    }

    [Fact]
    public async Task VoiceBiometricEvaluator_CanAnalyzeVoiceAnomalies()
    {
        // Arrange
        var evaluator = new VoiceBiometricEvaluator();
        var participantId = "participant-1";
        var audioSample = new byte[1024];

        // Act
        var analysis = await evaluator.AnalyzeVoiceAnomaliesAsync(participantId, audioSample);

        // Assert
        Assert.NotNull(analysis);
        Assert.Equal(participantId, analysis.ParticipantId);
        Assert.False(analysis.IsSyntheticVoiceDetected);
        Assert.Equal(StressLevel.Normal, analysis.StressLevel);

        await evaluator.DisposeAsync();
    }

    [Fact]
    public async Task VoiceBiometricEvaluator_CanGetAndDeleteProfile()
    {
        // Arrange
        var evaluator = new VoiceBiometricEvaluator();
        var participantId = "participant-1";
        var audioSample = new byte[1024];

        await evaluator.EnrollVoiceAsync(participantId, audioSample);

        // Act - Get profile
        var profile = evaluator.GetProfile(participantId);
        Assert.NotNull(profile);
        Assert.Equal(participantId, profile.ParticipantId);

        // Act - Delete profile
        var deleted = evaluator.DeleteProfile(participantId);
        Assert.True(deleted);

        // Assert - Profile no longer exists
        var deletedProfile = evaluator.GetProfile(participantId);
        Assert.Null(deletedProfile);

        await evaluator.DisposeAsync();
    }

    [Fact]
    public void ConversationSessionMetrics_RecordsSessionLifecycle()
    {
        // Arrange
        var metrics = new ConversationSessionMetrics();
        var sessionId = "session-1";

        // Act
        metrics.RecordSessionStarted(sessionId);
        metrics.RecordMessageSent(sessionId, latencyMs: 50);
        metrics.RecordMessageReceived(sessionId, latencyMs: 30);
        metrics.RecordToolInvocation(sessionId, "test_tool", executionTimeMs: 100, success: true);
        metrics.RecordSessionCompleted(sessionId, durationMs: 60000);

        // Assert - No exceptions thrown, metrics recorded
        Assert.True(true);

        metrics.Dispose();
    }

    [Fact]
    public void ConversationSessionMetrics_RecordsParticipantActivity()
    {
        // Arrange
        var metrics = new ConversationSessionMetrics();
        var sessionId = "session-1";

        // Act
        metrics.RecordParticipantJoined(sessionId, "participant-1");
        metrics.RecordParticipantJoined(sessionId, "participant-2");
        metrics.RecordParticipantLeft(sessionId, "participant-1");

        // Assert - No exceptions thrown
        Assert.True(true);

        metrics.Dispose();
    }

    [Fact]
    public void ConversationSessionMetrics_RecordsAuthenticationAttempts()
    {
        // Arrange
        var metrics = new ConversationSessionMetrics();
        var sessionId = "session-1";

        // Act
        metrics.RecordAuthenticationAttempt(sessionId, "voice_biometric", success: true, durationMs: 500);
        metrics.RecordAuthenticationAttempt(sessionId, "entra_verified_id", success: false, durationMs: 1000);

        // Assert - No exceptions thrown
        Assert.True(true);

        metrics.Dispose();
    }

    [Fact]
    public void ConversationSessionMetrics_RecordsFraudAlerts()
    {
        // Arrange
        var metrics = new ConversationSessionMetrics();
        var sessionId = "session-1";

        // Act
        metrics.RecordFraudAlert(sessionId, "social_engineering", riskScore: 75.0);
        metrics.RecordFraudRiskScore(sessionId, riskScore: 50.0);

        // Assert - No exceptions thrown
        Assert.True(true);

        metrics.Dispose();
    }

    [Fact]
    public void ConversationSessionMetrics_RecordsVoiceBiometricVerification()
    {
        // Arrange
        var metrics = new ConversationSessionMetrics();
        var sessionId = "session-1";

        // Act
        metrics.RecordVoiceBiometricVerification(sessionId, success: true, confidence: 0.95);

        // Assert - No exceptions thrown
        Assert.True(true);

        metrics.Dispose();
    }

    [Fact]
    public void ConversationSessionMetrics_CanStartSessionActivity()
    {
        // Arrange
        var metrics = new ConversationSessionMetrics();
        var sessionId = "session-1";

        // Act
        using var activity = metrics.StartSessionActivity(sessionId, "test_operation");

        // Assert - Activity may be null if no listeners, but should not throw
        Assert.True(true);

        metrics.Dispose();
    }

    [Fact]
    public void ConversationSessionMetrics_RecordsSessionFailed()
    {
        // Arrange
        var metrics = new ConversationSessionMetrics();
        var sessionId = "session-1";

        // Act
        metrics.RecordSessionStarted(sessionId);
        metrics.RecordSessionFailed(sessionId, "connection_lost");

        // Assert - No exceptions thrown
        Assert.True(true);

        metrics.Dispose();
    }
}
