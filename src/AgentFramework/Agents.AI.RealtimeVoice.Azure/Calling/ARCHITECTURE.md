# ContactCenterConversationHub Architecture

## Overview

The ContactCenterConversationHub implements a **layered scope architecture** similar to SignalR, enabling session, participant, and transport management across different DI scopes.

## Scope Hierarchy

```
┌─────────────────────────────────────────────┐
│ ContactCenterConversationHub (Singleton)    │
│ - Manages session lifecycle                 │
│ - Creates scoped sessions                   │
└─────────────┬───────────────────────────────┘
              │
              │ Creates with dedicated scope
              ▼
┌─────────────────────────────────────────────┐
│ ContactCenterConversationSession (Scoped)   │
│ - Owns IServiceScope for session lifetime   │
│ - Manages SessionParticipantContext         │
│ - Exposes SessionServices property          │
└─────────────┬───────────────────────────────┘
              │
              │ Creates and manages
              ▼
┌─────────────────────────────────────────────┐
│ SessionParticipantContext                   │
│ - Contains ConversationParticipant          │
│ - Manages multiple transports               │
│ - Aggregates user claims                    │
└─────────────┬───────────────────────────────┘
              │
              │ Contains wrapped transports
              ▼
┌─────────────────────────────────────────────┐
│ ParticipantContactTransport                 │
│ - Wraps IChannelTransport                   │
│ - Provides scoped service access            │
│ - Includes identity/claims                  │
└─────────────────────────────────────────────┘
```

## Key Components

### ContactCenterConversationHub (Singleton)
- Registered as singleton hosted service
- Creates and manages sessions
- Each session gets its own `IServiceScope`
- Sessions persist across multiple HTTP requests/scopes

### ContactCenterConversationSession (Scoped)
- Owns an `IServiceScope` for its lifetime
- Provides `SessionServices` property for accessing scoped services
- Creates `SessionParticipantContext` for each participant
- Supports adding transports from different request scopes

### SessionParticipantContext
- Represents a participant within a session
- **Implements `IParticipantContactTransport`** to act as a composite transport
- Manages multiple `IParticipantContactTransport` instances
- Broadcasts send operations to all underlying transports
- Aggregates capabilities (audio, messaging) from all transports
- Aggregates user claims from all transports
- Can be disposed independently of session
- Enables treating a participant as a single transport regardless of how many actual transports it has

### ParticipantContactTransport
- Wraps `IChannelTransport` with scoped service access
- Implements `IParticipantContactTransport` interface
- Provides `GetService()` method for DI access
- Supports different service providers per transport

## Cross-Scope Scenarios

The architecture supports adding participants and transports from different scopes:

### Scenario 1: API Creates Session
```csharp
// In API endpoint (Scope A)
var session = await hub.GetOrCreateSessionAsync("session-123");
var participant = new ConversationParticipant("agent-1", ParticipantType.Agent);
await session.AddParticipantAsync(participant);
```

### Scenario 2: Website Adds Participant via SignalR
```csharp
// In SignalR Hub OnConnectedAsync (Scope B)
var session = await hub.GetOrCreateSessionAsync("session-123");
var transport = new SignalRTransport(...);
await session.AddTransportToParticipantAsync(
    "agent-1", 
    transport, 
    Context.GetHttpContext().RequestServices);
```

### Scenario 3: Webhook Adds Transport
```csharp
// In webhook endpoint (Scope C)
var session = hub.GetSession("session-123");
if (session is not null)
{
    var transport = new AcsWebsocketTransport(webSocket, callProps, httpContext.RequestServices);
    await session.AddTransportToParticipantAsync(
        "customer-1", 
        transport, 
        httpContext.RequestServices);
}
```

### Scenario 4: Broadcasting to a Participant
```csharp
// SessionParticipantContext implements IParticipantContactTransport
// You can send to all of a participant's transports at once
var participantContext = session.GetParticipantContext("agent-1");
if (participantContext is not null)
{
    // This broadcasts to ALL transports (Teams, ACS phone, SignalR, etc.)
    var message = new MessageUpdate
    {
        CreatedAt = DateTimeOffset.UtcNow,
        SenderParticipantId = "system",
        Role = ChatRole.System.ToString(),
        Contents = [new TextContent("New participant joined")]
    };
    await participantContext.SendMessageAsync(message);
}
```

## Disposal Chain

Proper disposal ensures no resource leaks:

1. **Hub Shutdown**: `ContactCenterConversationHub.StopAsync()`
   - Closes all active sessions
   
2. **Session Disposal**: `ContactCenterConversationSession.DisposeAsync()`
   - Disposes all participant contexts
   - Disposes all transports
   - **Disposes the session scope** (critical!)
   - Disposes metrics and telemetry
   
3. **Participant Context Disposal**: `SessionParticipantContext.DisposeAsync()`
   - Disposes all wrapped transports
   
4. **Transport Disposal**: Individual transport cleanup

## Benefits

✅ **Scope Isolation**: Each session has its own scope for scoped services  
✅ **Cross-Scope Support**: Transports can be added from different request scopes  
✅ **Resource Management**: Proper disposal chain prevents leaks  
✅ **Service Access**: Transports can access their original request's services  
✅ **Familiar Pattern**: Similar to SignalR's Hub/Connection architecture  
✅ **Composite Pattern**: Participant contexts act as broadcast transports to all underlying transports  
✅ **Simplified Broadcasting**: Send to all participant transports with a single call  

## Usage Tips

1. **Always use the overload with IServiceProvider** when adding transports from webhooks or different scopes
2. **Don't dispose the session manually** - let the Hub manage session lifecycle
3. **Use `GetSession()` to retrieve existing sessions** - don't create new ones for existing session IDs
4. **Access scoped services via `session.SessionServices`** when needed within the session
5. **Use SessionParticipantContext for broadcasting** - it implements IParticipantContactTransport so you can send to all transports at once
