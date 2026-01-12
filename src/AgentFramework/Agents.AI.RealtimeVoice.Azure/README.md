# Enhanced Realtime Voice Agent Features

This directory contains enhanced features for the Azure Realtime Voice Agent, providing advanced capabilities for identity verification, fraud detection, authorization, and monitoring.

## Features

### 1. Background Agent Orchestration (`BackgroundAgents/`)

The `BackgroundAgentOrchestrator` enables a realtime voice agent to communicate with multiple background AI agents during a session. This supports multi-agent workflows where specialized agents handle specific tasks.

**Key Components:**
- `BackgroundAgentOrchestrator` - Manages background agents per session
- `BackgroundAgentRole` - Defines agent roles (FraudMonitor, Authorization, Compliance, etc.)
- `BackgroundAgentContext` - Tracks registered agents with their threads

**Usage Example:**
```csharp
var orchestrator = new BackgroundAgentOrchestrator(logger);

// Register a fraud detection agent
var fraudAgentId = await orchestrator.RegisterAgentAsync(
    fraudAgent,
    thread: null,
    role: BackgroundAgentRole.FraudMonitor,
    cancellationToken);

// Send a message to the fraud agent
var messages = new[] { new ChatMessage(ChatRole.User, "Check this transaction") };
var response = await orchestrator.SendToAgentAsync(fraudAgentId, messages);

// Broadcast to all fraud monitors
var responses = await orchestrator.BroadcastToRoleAsync(
    BackgroundAgentRole.FraudMonitor,
    messages);
```

### 2. Session-Scoped Tools (`SessionTools/`)

The `SessionToolContext` provides session-specific tools and data storage that can be accessed by the realtime agent during a conversation.

**Key Components:**
- `SessionToolContext` - Stores session data and tools
- `ISessionToolContextFactory` - Factory for creating tool contexts

**Usage Example:**
```csharp
var toolContext = new SessionToolContext(sessionId, participantId);

// Store session-specific data
toolContext.SetData("user_tier", "premium");
toolContext.SetData("auth_level", 2);

// Add session-scoped tools
var transferTool = AIFunctionFactory.Create(
    async (decimal amount, string recipient) => 
    {
        var tier = toolContext.GetData<string>("user_tier");
        // Use session context in tool execution
        return $"Transfer ${amount} to {recipient}";
    },
    "transfer_funds");

toolContext.AddTool(transferTool);
```

### 3. Entra Identity Verification (`IdentityVerification/`)

The `EntraIdentityVerificationService` integrates with Microsoft Entra Verified ID to securely verify participant identities during voice conversations.

**Key Components:**
- `IEntraIdentityVerificationService` - Service interface
- `EntraIdentityVerificationService` - Implementation
- `VerificationSession` - Tracks verification sessions
- `VerificationType` - Types: EntraVerifiedID, ManagedIdentity, VoiceBiometric

**Usage Example:**
```csharp
var verificationService = new EntraIdentityVerificationService(logger);

// Initiate verification
var request = new VerificationRequest
{
    Type = VerificationType.EntraVerifiedID,
    RequiredClaims = new List<string> { "email", "name", "employee_id" },
    ExpirationMinutes = 10
};

var session = await verificationService.InitiateVerificationAsync(
    participantId,
    request,
    cancellationToken);

// Present QR code or link to user
// session.PresentationRequestUrl

// Later, verify the credential
var result = await verificationService.VerifyCredentialAsync(
    session.SessionId,
    credential,
    cancellationToken);

if (result.Success && result.Status == VerificationStatus.Verified)
{
    var identity = result.VerifiedIdentity;
    // Use verified identity claims
}
```

### 4. Voice Approval Middleware (`Authorization/`)

The `VoiceApprovalMiddleware` handles voice-based authorization and approval workflows, enabling the agent to request and wait for participant confirmations.

**Key Components:**
- `VoiceApprovalMiddleware` - Manages approval requests
- `ApprovalRequest` - Represents an approval request
- `ApprovalResult` - Result of approval processing
- `ApprovalStatus` - Pending, Approved, Denied, Expired, Cancelled

**Usage Example:**
```csharp
var approvalMiddleware = new VoiceApprovalMiddleware(logger);

// Request approval for a sensitive action
var context = new ApprovalContext 
{ 
    TimeoutSeconds = 30,
    RequiresVoiceConfirmation = true
};

var request = await approvalMiddleware.RequestApprovalAsync(
    sessionId,
    participantId,
    "transfer_funds",
    context);

// Agent asks: "Do you approve the transfer of $500 to John?"
// Wait for participant response in background

// When participant responds
var result = await approvalMiddleware.ProcessApprovalResponseAsync(
    request.RequestId,
    approved: true,
    participantResponse: "Yes, I approve");

if (result.Approved)
{
    // Proceed with the action
}
```

### 5. Fraud Detection Monitoring (`Monitoring/FraudDetectionMonitor.cs`)

The `FraudDetectionMonitor` analyzes conversations in real-time for fraud indicators and suspicious behavior.

**Key Components:**
- `FraudDetectionMonitor` - Monitors conversation turns
- `FraudAssessment` - Risk assessment for a session
- `FraudIndicatorType` - Types of fraud indicators
- `FraudRiskLevel` - None, Low, Medium, High, Critical

**Usage Example:**
```csharp
var fraudMonitor = new FraudDetectionMonitor(options, logger);

// Analyze each conversation turn
var turn = new ConversationTurn
{
    Timestamp = DateTimeOffset.UtcNow,
    UserMessage = "What is my password? I need to verify urgently.",
    AgentResponse = "I cannot provide password information."
};

var assessment = await fraudMonitor.AnalyzeTurnAsync(sessionId, turn);

if (assessment.RiskLevel >= FraudRiskLevel.High)
{
    // Alert human operator
    // assessment.RiskScore
    // assessment.FraudIndicators
    // assessment.SocialEngineeringAttempts
}
```

**Detected Patterns:**
- Sensitive information requests (passwords, SSN, credit cards)
- Social engineering attempts (urgent action, verify account)
- Authentication bypass attempts
- Rapid-fire requests
- Unusual behavior patterns

### 6. Voice Biometric Evaluation (`Monitoring/VoiceBiometricEvaluator.cs`)

The `VoiceBiometricEvaluator` performs voice biometric verification and anomaly detection.

**Key Components:**
- `VoiceBiometricEvaluator` - Evaluates voice characteristics
- `VoiceBiometricProfile` - User's voice profile
- `VoiceEnrollmentResult` - Enrollment status
- `VoiceVerificationResult` - Verification outcome
- `VoiceAnomalyAnalysis` - Anomaly detection results

**Usage Example:**
```csharp
var biometricEvaluator = new VoiceBiometricEvaluator(options, logger);

// Enrollment phase (typically done separately)
for (int i = 0; i < 3; i++)
{
    var audioSample = GetAudioSample();
    var enrollment = await biometricEvaluator.EnrollVoiceAsync(
        participantId,
        audioSample);
    
    if (enrollment.IsComplete)
        break;
}

// Verification during conversation
var audioSample = GetLiveAudioSample();
var verification = await biometricEvaluator.VerifyVoiceAsync(
    participantId,
    audioSample);

if (verification.IsMatch && verification.ConfidenceScore >= 0.85)
{
    // Voice verified
}

// Anomaly detection
var anomalyAnalysis = await biometricEvaluator.AnalyzeVoiceAnomaliesAsync(
    participantId,
    audioSample);

if (anomalyAnalysis.IsSyntheticVoiceDetected)
{
    // Potential deepfake or synthetic voice
}
```

### 7. Conversation Session Metrics (`Monitoring/ConversationSessionMetrics.cs`)

The `ConversationSessionMetrics` provides comprehensive OpenTelemetry metrics for monitoring conversation health and performance.

**Key Metrics:**
- **Counters:** Sessions started/completed/failed, messages, tool invocations, auth attempts, fraud alerts
- **Gauges:** Active sessions, active participants
- **Histograms:** Session duration, message latency, tool execution time, fraud risk scores

**Usage Example:**
```csharp
var metrics = new ConversationSessionMetrics();

// Record session lifecycle
metrics.RecordSessionStarted(sessionId, tags);
metrics.RecordParticipantJoined(sessionId, participantId);

// Record message activity
metrics.RecordMessageSent(sessionId, latencyMs: 50);
metrics.RecordMessageReceived(sessionId, latencyMs: 30);

// Record tool invocations
metrics.RecordToolInvocation(
    sessionId,
    toolName: "transfer_funds",
    executionTimeMs: 250,
    success: true);

// Record authentication
metrics.RecordAuthenticationAttempt(
    sessionId,
    method: "EntraVerifiedID",
    success: true,
    durationMs: 1500);

// Record fraud alerts
metrics.RecordFraudAlert(
    sessionId,
    alertType: "SocialEngineering",
    riskScore: 65.0);

// Record voice biometrics
metrics.RecordVoiceBiometricVerification(
    sessionId,
    success: true,
    confidence: 0.92);

// Complete session
metrics.RecordSessionCompleted(sessionId, durationMs: 120000);
```

### 8. Enhanced Session Context (`Calling/EnhancedSessionContext.cs`)

The `EnhancedSessionContext` integrates all features into a unified session management context.

**Usage Example:**
```csharp
// Create enhanced session context
var sessionContext = new EnhancedSessionContext(sessionId, logger);

// Access all features through the context
var backgroundAgents = sessionContext.BackgroundAgents;
var identityVerification = sessionContext.IdentityVerification;
var voiceApproval = sessionContext.VoiceApproval;
var fraudDetection = sessionContext.FraudDetection;
var voiceBiometrics = sessionContext.VoiceBiometrics;
var metrics = sessionContext.Metrics;

// Get or create tool context for a participant
var toolContext = sessionContext.GetOrCreateToolContext(participantId);

// Use the integrated features
await sessionContext.IdentityVerification.InitiateVerificationAsync(...);
await sessionContext.BackgroundAgents.RegisterAgentAsync(...);
await sessionContext.VoiceApproval.RequestApprovalAsync(...);

// Cleanup
await sessionContext.DisposeAsync();
```

## Dependency Injection Setup

```csharp
// In Startup.cs or Program.cs
services.AddEnhancedRealtimeVoice(options =>
{
    options.EnableEntraVerification = true;
    options.EnableFraudDetection = true;
    options.EnableVoiceBiometrics = true;
    options.EnableBackgroundAgents = true;
    options.EnableSessionTools = true;
    options.EnableVoiceApproval = true;
    options.EnableMetrics = true;
    
    options.FraudDetection.HighRiskThreshold = 50.0;
    options.VoiceBiometrics.VerificationThreshold = 0.85;
});
```

## Integration with ContactCenterConversationSession

To integrate these features with the existing `ContactCenterConversationSession`, you can:

1. Add `EnhancedSessionContext` as a property or service in the session
2. Wire up events to trigger fraud detection and metrics recording
3. Integrate voice approval into the tool execution pipeline
4. Use background agents for monitoring and assistance

Example integration:
```csharp
public class ContactCenterConversationSession
{
    private EnhancedSessionContext? _enhancedContext;
    
    public async Task InitializeEnhancedFeaturesAsync()
    {
        _enhancedContext = new EnhancedSessionContext(_sessionId, _loggerFactory);
        
        // Register fraud monitor as background agent
        var fraudMonitor = new FraudDetectionAgent();
        await _enhancedContext.BackgroundAgents.RegisterAgentAsync(
            fraudMonitor,
            role: BackgroundAgentRole.FraudMonitor);
    }
    
    // In message handling
    private async Task HandleMessageAsync(ChatMessage message)
    {
        // Record metrics
        _enhancedContext?.Metrics.RecordMessageReceived(_sessionId, latencyMs);
        
        // Check for fraud
        var turn = new ConversationTurn { UserMessage = message.Text };
        var assessment = await _enhancedContext.FraudDetection.AnalyzeTurnAsync(
            _sessionId, turn);
            
        if (assessment.RiskLevel >= FraudRiskLevel.High)
        {
            // Escalate to human operator
        }
    }
}
```

## Testing

See `test/Agents.AI.RealtimeVoice.Azure.Tests/EnhancedRealtimeVoiceTests.cs` for comprehensive unit tests covering all features.

## Architecture Notes

- All implementations are **localized to Agents.AI.RealtimeVoice.Azure**
- **No modifications** to base Agents.AI RealtimeVoice implementation
- Follows existing patterns (middleware, delegating agents, etc.)
- Designed for easy integration with existing `ContactCenterConversationSession`
- Thread-safe implementations with proper locking
- Comprehensive logging and telemetry
- Async/await throughout for performance
- Proper disposal patterns for resource cleanup

## Security Considerations

- Identity verification integrates with Entra Verified ID (implementation needs actual API calls)
- Voice approval requires proper timeout and expiration handling
- Fraud detection patterns should be tuned for your specific use case
- Voice biometrics should use production-grade algorithms (current implementation is simplified)
- All sensitive operations should be audited through telemetry
- Consider rate limiting and throttling for fraud prevention

## Future Enhancements

- Integrate with actual Microsoft Entra Verified ID API
- Add production-grade voice biometric algorithms
- Implement ML-based fraud detection models
- Add support for incremental consent workflows
- Create dashboard templates for Grafana/Azure Monitor
- Add more sophisticated authorization policies
- Implement voice stress analysis
- Add support for multi-factor authentication flows
