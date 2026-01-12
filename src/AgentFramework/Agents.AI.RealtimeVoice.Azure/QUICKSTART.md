# Enhanced Realtime Voice Agent - Quick Start Guide

## Installation

### 1. Add to Dependency Injection

In your `Program.cs` or `Startup.cs`:

```csharp
using Agents.AI.RealtimeVoice.Azure;

builder.Services.AddEnhancedRealtimeVoice(options =>
{
    // Enable features you need
    options.EnableEntraVerification = true;
    options.EnableFraudDetection = true;
    options.EnableVoiceBiometrics = true;
    options.EnableBackgroundAgents = true;
    options.EnableSessionTools = true;
    options.EnableVoiceApproval = true;
    options.EnableMetrics = true;
    
    // Configure thresholds
    options.FraudDetection.HighRiskThreshold = 50.0;
    options.VoiceBiometrics.VerificationThreshold = 0.85;
});
```

## Quick Examples

### Identity Verification (30 seconds)

```csharp
var context = new EnhancedSessionContext(sessionId, logger);

// Request verification
var request = new VerificationRequest
{
    Type = VerificationType.EntraVerifiedID,
    RequiredClaims = new List<string> { "email", "name" }
};

var session = await context.IdentityVerification
    .InitiateVerificationAsync(participantId, request);

// Present QR code to user...

// Verify
var result = await context.IdentityVerification
    .VerifyCredentialAsync(session.SessionId, credential);

if (result.Success)
{
    // User verified!
    var identity = result.VerifiedIdentity;
}
```

### Fraud Detection (15 seconds)

```csharp
var context = new EnhancedSessionContext(sessionId, logger);

// Analyze conversation
var turn = new ConversationTurn
{
    Timestamp = DateTimeOffset.UtcNow,
    UserMessage = userInput,
    AgentResponse = agentOutput
};

var assessment = await context.FraudDetection
    .AnalyzeTurnAsync(sessionId, turn);

if (assessment.RiskLevel >= FraudRiskLevel.High)
{
    // Alert! High fraud risk detected
    // assessment.RiskScore
    // assessment.FraudIndicators
}
```

### Voice Approval (20 seconds)

```csharp
var context = new EnhancedSessionContext(sessionId, logger);

// Request approval
var approvalContext = new ApprovalContext
{
    Description = "Transfer $500 to John Doe",
    TimeoutSeconds = 30
};

var request = await context.VoiceApproval.RequestApprovalAsync(
    sessionId,
    participantId,
    "transfer_funds",
    approvalContext);

// Agent asks: "Do you approve the transfer? Say yes or no."

// Wait for response
var result = await context.VoiceApproval.WaitForApprovalAsync(
    request.RequestId);

if (result.Approved)
{
    // Execute action
}
```

### Background Agents (25 seconds)

```csharp
var context = new EnhancedSessionContext(sessionId, logger);

// Register fraud monitor
var fraudAgentId = await context.BackgroundAgents.RegisterAgentAsync(
    fraudAgent,
    role: BackgroundAgentRole.FraudMonitor);

// Consult fraud monitor
var messages = new[] 
{ 
    new ChatMessage(ChatRole.User, "Is this transaction suspicious?") 
};

var responses = await context.BackgroundAgents.BroadcastToRoleAsync(
    BackgroundAgentRole.FraudMonitor,
    messages);

// Process responses...
```

### Voice Biometrics (30 seconds)

```csharp
var context = new EnhancedSessionContext(sessionId, logger);

// Enrollment (do this once)
for (int i = 0; i < 3; i++)
{
    var sample = GetAudioSample();
    var enrollment = await context.VoiceBiometrics
        .EnrollVoiceAsync(participantId, sample);
    
    if (enrollment.IsComplete) break;
}

// Verification (do this each call)
var audioSample = GetLiveAudio();
var verification = await context.VoiceBiometrics
    .VerifyVoiceAsync(participantId, audioSample);

if (verification.IsMatch && verification.ConfidenceScore >= 0.85)
{
    // Voice verified!
}
```

### Session Tools (20 seconds)

```csharp
var context = new EnhancedSessionContext(sessionId, logger);

// Get tool context
var toolContext = context.GetOrCreateToolContext(participantId);

// Store session data
toolContext.SetData("user_tier", "premium");
toolContext.SetData("verified", true);

// Add session-scoped tool
var tool = AIFunctionFactory.Create(
    (decimal amount) =>
    {
        var tier = toolContext.GetData<string>("user_tier");
        var verified = toolContext.GetData<bool>("verified");
        
        if (!verified) return "Verification required";
        
        return $"Transfer approved for {tier} user";
    },
    "transfer");

toolContext.AddTool(tool);
```

### OpenTelemetry Metrics (15 seconds)

```csharp
var context = new EnhancedSessionContext(sessionId, logger);

// Record session lifecycle
context.Metrics.RecordSessionStarted(sessionId);

// Record messages
context.Metrics.RecordMessageSent(sessionId, latencyMs: 50);
context.Metrics.RecordMessageReceived(sessionId, latencyMs: 30);

// Record tool execution
context.Metrics.RecordToolInvocation(
    sessionId,
    "transfer_funds",
    executionTimeMs: 250,
    success: true);

// Record fraud alerts
context.Metrics.RecordFraudAlert(
    sessionId,
    alertType: "SocialEngineering",
    riskScore: 65.0);

// Record session end
context.Metrics.RecordSessionCompleted(sessionId, durationMs: 120000);
```

## Common Patterns

### Pattern 1: Secure High-Value Transaction

```csharp
var context = new EnhancedSessionContext(sessionId, logger);

// 1. Verify identity
var verification = await VerifyIdentityAsync(context, participantId);
if (!verification.Success) return "Identity verification failed";

// 2. Check voice biometric
var voiceVerified = await VerifyVoiceAsync(context, participantId, audio);
if (!voiceVerified) return "Voice verification failed";

// 3. Monitor for fraud
var fraudCheck = await CheckFraudAsync(context, conversationTurn);
if (fraudCheck.RiskLevel >= FraudRiskLevel.High)
    return "Transaction blocked - high fraud risk";

// 4. Request approval
var approval = await RequestApprovalAsync(context, participantId, "transfer");
if (!approval) return "Transaction cancelled";

// 5. Execute with session tools
var toolContext = context.GetOrCreateToolContext(participantId);
var result = await toolContext.ExecuteToolAsync("transfer_funds", amount);

// 6. Record metrics
context.Metrics.RecordToolInvocation(sessionId, "transfer_funds", time, true);

return result;
```

### Pattern 2: Real-time Fraud Monitoring

```csharp
var context = new EnhancedSessionContext(sessionId, logger);

// Register fraud monitor agent
await context.BackgroundAgents.RegisterAgentAsync(
    fraudMonitorAgent,
    role: BackgroundAgentRole.FraudMonitor);

// On each conversation turn
async Task OnMessageAsync(string userMessage, string agentResponse)
{
    // Analyze for fraud
    var turn = new ConversationTurn
    {
        Timestamp = DateTimeOffset.UtcNow,
        UserMessage = userMessage,
        AgentResponse = agentResponse
    };
    
    var assessment = await context.FraudDetection.AnalyzeTurnAsync(
        sessionId, turn);
    
    // If high risk, consult background agent
    if (assessment.RiskLevel >= FraudRiskLevel.High)
    {
        var consultation = await context.BackgroundAgents.BroadcastToRoleAsync(
            BackgroundAgentRole.FraudMonitor,
            new[] { new ChatMessage(ChatRole.User, 
                $"Risk score {assessment.RiskScore}. Should I proceed?") });
        
        // Take action based on agent recommendation
    }
    
    // Record metrics
    context.Metrics.RecordFraudRiskScore(sessionId, assessment.RiskScore);
}
```

### Pattern 3: Progressive Identity Verification

```csharp
var context = new EnhancedSessionContext(sessionId, logger);

// Level 1: Basic info
var basicVerified = await VerifyBasicInfoAsync(participantId);

// Level 2: Voice biometric
if (requiresHigherSecurity)
{
    var voiceVerified = await context.VoiceBiometrics
        .VerifyVoiceAsync(participantId, audioSample);
    
    if (!voiceVerified.IsMatch)
    {
        // Escalate to Level 3
    }
}

// Level 3: Entra Verified ID
if (requiresMaxSecurity)
{
    var entraVerification = await context.IdentityVerification
        .InitiateVerificationAsync(participantId, request);
    
    // Present QR code and wait...
}

// Store verification level in session
var toolContext = context.GetOrCreateToolContext(participantId);
toolContext.SetData("verification_level", currentLevel);
```

## Troubleshooting

### Issue: Approval times out

```csharp
// Increase timeout
var context = new ApprovalContext 
{ 
    TimeoutSeconds = 60  // Increased from 30
};
```

### Issue: Fraud detection too sensitive

```csharp
// Adjust thresholds in configuration
services.AddEnhancedRealtimeVoice(options =>
{
    options.FraudDetection.HighRiskThreshold = 75.0;  // Higher = less sensitive
});
```

### Issue: Voice verification failing

```csharp
// Lower threshold or re-enroll
services.AddEnhancedRealtimeVoice(options =>
{
    options.VoiceBiometrics.VerificationThreshold = 0.80;  // Lower threshold
});

// Or re-enroll with better quality samples
```

## Best Practices

1. **Always dispose contexts**
   ```csharp
   await using var context = new EnhancedSessionContext(sessionId, logger);
   // Use context...
   ```

2. **Record metrics for monitoring**
   ```csharp
   context.Metrics.RecordSessionStarted(sessionId);
   // ... do work ...
   context.Metrics.RecordSessionCompleted(sessionId, duration);
   ```

3. **Handle timeouts gracefully**
   ```csharp
   try
   {
       var result = await context.VoiceApproval.WaitForApprovalAsync(
           requestId, cancellationToken);
   }
   catch (OperationCanceledException)
   {
       // Handle timeout
   }
   ```

4. **Validate before executing sensitive operations**
   ```csharp
   var verified = await VerifyIdentityAsync();
   var fraudCheck = await CheckFraudAsync();
   var approved = await GetApprovalAsync();
   
   if (verified && fraudCheck.RiskLevel == Low && approved)
   {
       // Execute
   }
   ```

5. **Use background agents for monitoring**
   ```csharp
   // Register monitoring agents at session start
   await context.BackgroundAgents.RegisterAgentAsync(
       fraudMonitor, BackgroundAgentRole.FraudMonitor);
   await context.BackgroundAgents.RegisterAgentAsync(
       complianceMonitor, BackgroundAgentRole.Compliance);
   ```

## Next Steps

- Read the full [README.md](./README.md) for detailed feature documentation
- Review [ARCHITECTURE.md](./ARCHITECTURE.md) for system design and data flows
- Check [Examples/EnhancedRealtimeAgentExample.cs](./Examples/EnhancedRealtimeAgentExample.cs) for complete scenarios
- See [test/EnhancedRealtimeVoiceTests.cs](../../test/Agents.AI.RealtimeVoice.Azure.Tests/EnhancedRealtimeVoiceTests.cs) for unit tests

## Support

For issues or questions:
1. Check the documentation files in this directory
2. Review the example code
3. Examine the unit tests for usage patterns
4. Open an issue on the repository
