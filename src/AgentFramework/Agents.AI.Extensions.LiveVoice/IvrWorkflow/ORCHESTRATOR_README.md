# IVR Orchestrator

## Overview

The IVR Orchestrator is a chat-based AI control plane that coordinates low-latency voice interactions through the Agent Framework while maintaining the deterministic IVR workflow as the authoritative source for step progression, validation, and retries.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                 ContactCenterConversationSession                 │
│                                                                   │
│  ┌──────────────────┐         ┌──────────────────┐             │
│  │  Realtime Voice  │ ──────► │  Orchestrator    │             │
│  │  Agent Transport │ Transcript Participant    │             │
│  │  (Audio + Text)  │         │  (Control Plane) │             │
│  └──────────────────┘         └────────┬─────────┘             │
│         │                               │                        │
│         │ Audio (Low Latency)          │ Tools:                │
│         │                               │ - Submit IVR Input    │
│         ▼                               │ - Change Persona      │
│  ┌──────────────────┐                  │ - Handoff to Human   │
│  │  All Other       │ ◄────────────────┘ - Get Workflow State │
│  │  Participants    │ Prompts (TTS)                            │
│  └──────────────────┘                                           │
│                                                                   │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │              IvrWorkflowSession (Deterministic)           │  │
│  │  - Step Progression                                        │  │
│  │  - Input Validation                                        │  │
│  │  - Guards & Retries                                        │  │
│  │  - State Management                                        │  │
│  └──────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

## Key Components

### IvrOrchestrator

The main orchestrator class that:
- Wraps an `IvrWorkflowSession` for deterministic step control
- Uses a chat-based AI agent (IChatClient) as the control plane
- Routes transcripts from realtime voice agents to the IVR workflow
- Surfaces IVR prompts back through transports for TTS conversion
- Fires events for persona changes, handoffs, and workflow lifecycle

**Events:**
- `OnPromptReady` - IVR prompt ready for TTS/voice output
- `OnPersonaChangeRequested` - Request to switch realtime agent persona
- `OnHandoffRequested` - Request to escalate to human operator
- `OnWorkflowCompleted` - Workflow completed successfully
- `OnWorkflowFailed` - Workflow failed

### IvrOrchestratorTools

AI tools that the orchestrator can invoke:
- `SubmitIvrInputAsync` - Submit user input to IVR workflow
- `ChangeRealtimePersonaAsync` - Switch voice agent persona
- `HandoffToHumanAsync` - Escalate to human operator
- `GetIvrWorkflowStateAsync` - Query current workflow state

### OrchestratorSessionActivator

A `IContactCenterConversationSessionActivator` implementation that:
- Creates a session with an orchestrator participant
- Wires transcript flow: RealtimeAIAgentTransport → Orchestrator → IvrWorkflowSession
- Routes IVR prompts back through transports
- Uses fire-and-forget for non-critical operations to maintain low latency

## Usage

### 1. Register Services

In your `Program.cs` or startup configuration:

```csharp
builder.Services.AddIvrOrchestratorServices();
```

### 2. Create a Session Activator

Replace or augment your existing session activator:

```csharp
services.AddSingleton<IContactCenterConversationSessionActivator, OrchestratorSessionActivator>();
```

Or use conditionally based on configuration:

```csharp
services.AddSingleton<IContactCenterConversationSessionActivator>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var useOrchestrator = config.GetValue<bool>("UseOrchestratorMode");
    
    if (useOrchestrator)
    {
        return sp.GetRequiredService<OrchestratorSessionActivator>();
    }
    else
    {
        return sp.GetRequiredService<BiometricWorkflowActivator>();
    }
});
```

### 3. Define Your Workflow

Use the existing IVR workflow builder - no changes needed:

```csharp
var workflow = IvrWorkflowBuilder.Create("CallerIntentBiometric")
    .WithWelcomeMessage("Welcome to the contact center.")
    .AddInputStep(
        name: "CollectIntent",
        prompt: "What is the reason for your call?",
        stateKey: "callerIntent")
    .AddInputStep(
        name: "CollectName",
        prompt: "Please state your name.",
        stateKey: "callerName")
    .Build();
```

### 4. Orchestrator Operation Flow

1. **Session Start:**
   - OrchestratorSessionActivator creates ContactCenterConversationSession
   - Creates IvrWorkflowSession with your workflow
   - Creates IvrOrchestrator wrapping the workflow
   - Creates orchestrator participant in the session

2. **User Speaks (Transcript Flow):**
   - RealtimeAIAgentTransport captures audio → transcription
   - Transcript sent to orchestrator participant
   - Orchestrator processes transcript → IvrWorkflowSession
   - IVR workflow validates and processes input

3. **IVR Response (Prompt Flow):**
   - IVR workflow generates prompt (e.g., "What is your account number?")
   - Orchestrator receives prompt via OnPromptReady event
   - Orchestrator broadcasts prompt to all participants (except itself)
   - RealtimeAIAgentTransport receives text → TTS → audio to caller

4. **Low Latency Considerations:**
   - Orchestrator uses fire-and-forget for non-critical operations
   - Prompt broadcasting is immediate (no buffering)
   - IVR workflow remains synchronous for validation integrity
   - Background tasks for analytics, logging, etc.

## Example: CallerIntentBiometricWorkflow with Orchestrator

The existing `CallerIntentBiometricWorkflow` works with the orchestrator without modification:

```csharp
public sealed class OrchestratorSessionActivator : IContactCenterConversationSessionActivator
{
    private readonly IIvrWorkflowSessionFactory _workflowFactory;
    private readonly IVoiceBiometricEvaluator _biometricEvaluator;

    // ... constructor ...

    public ContactCenterConversationSession Create(
        string sessionId,
        IServiceScope sessionScope,
        ILoggerFactory loggerFactory)
    {
        var session = new ContactCenterConversationSession(/*...*/);
        
        _ = Task.Run(async () =>
        {
            // Create workflow
            var workflow = CallerIntentBiometricWorkflow.Create(sessionId, _biometricEvaluator);
            var ivrSession = _workflowFactory.CreateSession(sessionId, workflow);
            
            // Get chat client for orchestrator
            var chatClient = sessionScope.ServiceProvider.GetRequiredService<IChatClient>();
            
            // Create orchestrator
            var orchestrator = new IvrOrchestrator(sessionId, chatClient, ivrSession);
            
            // Wire up events
            orchestrator.OnPromptReady += async (sid, prompt, ct) =>
            {
                await BroadcastToParticipants(session, prompt, ct);
            };
            
            // Start
            await orchestrator.StartAsync();
        });
        
        return session;
    }
}
```

## Configuration Options

### Enable/Disable Orchestrator Mode

In `appsettings.json`:

```json
{
  "ContactCenter": {
    "UseOrchestratorMode": true,
    "OrchestratorOptions": {
      "EnablePersonaSwitching": true,
      "EnableHumanHandoff": true,
      "DefaultChatModel": "gpt-4o"
    }
  }
}
```

## Advanced Features

### Persona Switching

The orchestrator can request persona changes for different interaction styles:

```csharp
orchestrator.OnPersonaChangeRequested += async (personaName, reason, ct) =>
{
    // Remove current RealtimeAIAgentTransport
    await session.RemoveTransportFromParticipant(participantId, currentTransportId);
    
    // Create new transport with different agent configuration
    var newAgent = CreatePersonaAgent(personaName); // Your factory method
    var newTransport = new RealtimeAIAgentTransport(newAgent, thread, ...);
    
    // Add new transport
    await session.AddTransportToParticipant(participantId, newTransport);
};
```

### Human Handoff

When the orchestrator requests handoff:

```csharp
orchestrator.OnHandoffRequested += async (reason, ct) =>
{
    // Pause realtime agent
    await session.RemoveTransportFromParticipant(voiceParticipantId, realtimeTransportId);
    
    // Add human operator connection
    var operatorTransport = await CreateOperatorTransport();
    await session.AddTransportToParticipant(operatorParticipantId, operatorTransport);
    
    // Notify user
    await BroadcastToParticipants(session, 
        "Connecting you to an operator. Please hold.", ct);
};
```

## Testing

### Unit Tests

Test orchestrator behavior with mocked dependencies:

```csharp
[Fact]
public async Task Orchestrator_ForwardsTranscriptToIvrWorkflow()
{
    // Arrange
    var mockChatClient = new Mock<IChatClient>();
    var mockWorkflow = new Mock<IvrWorkflowSession>();
    var orchestrator = new IvrOrchestrator(sessionId, mockChatClient.Object, mockWorkflow.Object);
    
    // Act
    await orchestrator.ProcessTranscriptAsync("My name is John");
    
    // Assert
    mockWorkflow.Verify(w => w.ProcessMessageAsync("My name is John", It.IsAny<CancellationToken>()), Times.Once);
}
```

### Integration Tests

Test full session flow with orchestrator:

```csharp
[Fact]
public async Task Session_WithOrchestrator_ProcessesCallFlow()
{
    // Create session with orchestrator activator
    var activator = new OrchestratorSessionActivator(/*...*/);
    var session = activator.Create(sessionId, scope, loggerFactory);
    
    // Simulate user joining and speaking
    var participant = session.GetOrAddParticipant("caller");
    await participant.SendMessageAsync(new MessageUpdate 
    { 
        Contents = [new TextContent("I need help with my account")]
    });
    
    // Verify workflow processed and responded
    // ... assertions ...
}
```

## Performance Considerations

### Low-Latency Design

- **Fire-and-forget**: Non-critical operations (analytics, logging) use background tasks
- **No buffering**: Prompts are broadcast immediately when ready
- **Minimal overhead**: Orchestrator adds ~1-5ms latency for transcript routing
- **Direct IVR**: Workflow validation is synchronous to maintain integrity

### Monitoring

Monitor orchestrator performance:

```csharp
// Add metrics/telemetry
orchestrator.OnPromptReady += async (sid, prompt, ct) =>
{
    using var activity = activitySource.StartActivity("OrchestratorPrompt");
    activity?.SetTag("session.id", sid);
    activity?.SetTag("prompt.length", prompt.Length);
    
    var start = Stopwatch.GetTimestamp();
    await BroadcastToParticipants(session, prompt, ct);
    var elapsed = Stopwatch.GetElapsedTime(start);
    
    logger.LogDebug("Prompt broadcast latency: {LatencyMs}ms", elapsed.TotalMilliseconds);
};
```

## Troubleshooting

### Issue: Orchestrator not receiving transcripts

**Solution:** Ensure the orchestrator participant is added and hooked to message events:

```csharp
var orchestratorParticipant = session.GetOrAddParticipant("orchestrator");
orchestratorParticipant.OnMessageReceived(async (sourceId, message, ct) =>
{
    if (sourceId != "orchestrator")
    {
        await orchestrator.ProcessMessageUpdateAsync(message, ct);
    }
});
```

### Issue: IVR prompts not reaching caller

**Solution:** Verify prompt broadcasting excludes orchestrator:

```csharp
foreach (var participant in session.ParticipantContexts.Values)
{
    if (participant.ParticipantId != "orchestrator")
    {
        await participant.SendMessageAsync(promptMessage, ct);
    }
}
```

### Issue: High latency in prompt delivery

**Solution:** Use fire-and-forget for broadcasting:

```csharp
orchestrator.OnPromptReady += (sid, prompt, ct) =>
{
    _ = Task.Run(async () => await BroadcastToParticipants(session, prompt, ct), ct);
    return Task.CompletedTask;
};
```

## Future Enhancements

- **Tool execution context**: Allow tools to access session services directly
- **Multi-workflow orchestration**: Support switching between different workflows
- **Conversation memory**: Maintain context across workflow transitions
- **Advanced persona management**: Dynamic persona selection based on intent
- **Metrics dashboard**: Real-time monitoring of orchestrator performance
