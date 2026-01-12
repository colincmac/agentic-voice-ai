# Operator Dashboard API Reference

This document describes the API endpoints and SignalR hub events for the live call monitoring dashboard.

## REST API Endpoints

### Get Active Calls

```http
GET /api/operator/calls/active
```

Returns all currently active calls.

**Response:** `200 OK`
```json
[
  {
    "sessionId": "call_server123",
    "callConnectionId": "connection_abc",
    "startedAt": "2024-01-15T10:30:00Z",
    "endedAt": null,
    "status": "Active",
    "participants": [
      {
        "participantId": "+15551234567",
        "displayName": "John Doe",
        "participantType": "Customer",
        "role": "Caller",
        "channelType": "Phone",
        "joinedAt": "2024-01-15T10:30:00Z",
        "isMuted": false,
        "isOnHold": false,
        "isConnected": true
      },
      {
        "participantId": "agent_triage",
        "displayName": "Triage Agent",
        "participantType": "AIAgent",
        "role": "Assistant",
        "channelType": "VoiceAIAgent",
        "joinedAt": "2024-01-15T10:30:01Z",
        "isMuted": false,
        "isOnHold": false,
        "isConnected": true
      }
    ],
    "customerSentiment": 0.5,
    "agentSentiment": 0.8,
    "taskAdherenceScore": 0.9,
    "escalationRiskScore": 0.1,
    "activeTasks": ["greeting", "identity_verification"],
    "latestUtteranceSummary": "Customer asked about account balance",
    "duration": "00:05:23"
  }
]
```

### Get Call Details

```http
GET /api/operator/calls/{sessionId}
```

Returns details for a specific call.

**Response:** `200 OK` - Same format as individual call in the active calls array.

**Response:** `404 Not Found`
```json
{
  "message": "Call with session ID 'xxx' not found."
}
```

## SignalR Hub

### Connection

Connect to the operator dashboard hub at:

```
/operatorHub
```

### Client Events (Subscribe To)

#### CallStarted

Fired when a new call begins.

```typescript
connection.on("CallStarted", (summary: LiveCallSummary) => {
  // Add new call to dashboard
});
```

#### CallEnded

Fired when a call ends.

```typescript
connection.on("CallEnded", (data: { sessionId: string }) => {
  // Remove call from dashboard or mark as ended
});
```

#### CallHealthUpdated

Fired when health metrics are updated (after each utterance analysis).

```typescript
connection.on("CallHealthUpdated", (summary: LiveCallSummary) => {
  // Update health metrics display
});
```

#### CallDetails

Response to `GetCallDetails` or when subscribed to a specific session.

```typescript
connection.on("CallDetails", (summary: LiveCallSummary) => {
  // Update detailed view
});
```

#### CallNotFound

Response when requested call is not found.

```typescript
connection.on("CallNotFound", (data: { sessionId: string }) => {
  // Show error
});
```

### Server Methods (Invoke)

#### SubscribeToSession

Subscribe to detailed updates for a specific session.

```typescript
await connection.invoke("SubscribeToSession", sessionId);
```

#### UnsubscribeFromSession

Unsubscribe from session-specific updates.

```typescript
await connection.invoke("UnsubscribeFromSession", sessionId);
```

#### GetCallDetails

Request current state of a specific call.

```typescript
await connection.invoke("GetCallDetails", sessionId);
```

## TypeScript Types

```typescript
interface LiveCallSummary {
  sessionId: string;
  callConnectionId: string | null;
  startedAt: string; // ISO 8601
  endedAt: string | null; // ISO 8601
  status: LiveCallStatus;
  participants: LiveParticipantSummary[];
  
  // Health metrics (-1 to 1 for sentiment, 0 to 1 for others)
  customerSentiment: number | null;
  agentSentiment: number | null;
  taskAdherenceScore: number | null;
  escalationRiskScore: number | null;
  activeTasks: string[];
  latestUtteranceSummary: string | null;
  
  // Computed (TimeSpan as string)
  duration: string;
}

type LiveCallStatus = 
  | "Connecting"
  | "Active"
  | "OnHold"
  | "Ended"
  | "Failed";

interface LiveParticipantSummary {
  participantId: string;
  displayName: string | null;
  participantType: ParticipantType;
  role: ParticipantRole;
  channelType: CommunicationChannelType;
  joinedAt: string; // ISO 8601
  isMuted: boolean;
  isOnHold: boolean;
  isConnected: boolean;
}

type ParticipantType = 
  | "Customer"
  | "Agent"
  | "AIAgent"
  | "Supervisor"
  | "System";

type ParticipantRole =
  | "Caller"
  | "Callee"
  | "Observer"
  | "Assistant"
  | "Moderator";

type CommunicationChannelType =
  | "TeamsChatThread"
  | "Phone"
  | "ChatAIAgent"
  | "VoiceAIAgent"
  | "AcsUser"
  | "Unknown";
```

## Example Next.js Integration

```typescript
// hooks/useOperatorDashboard.ts
import { HubConnectionBuilder, HubConnection } from "@microsoft/signalr";
import { useState, useEffect, useCallback } from "react";

interface UseOperatorDashboardOptions {
  apiBaseUrl: string;
}

export function useOperatorDashboard({ apiBaseUrl }: UseOperatorDashboardOptions) {
  const [connection, setConnection] = useState<HubConnection | null>(null);
  const [activeCalls, setActiveCalls] = useState<LiveCallSummary[]>([]);
  const [selectedCall, setSelectedCall] = useState<LiveCallSummary | null>(null);
  const [isConnected, setIsConnected] = useState(false);

  useEffect(() => {
    const newConnection = new HubConnectionBuilder()
      .withUrl(`${apiBaseUrl}/operatorHub`)
      .withAutomaticReconnect()
      .build();

    // Handle events
    newConnection.on("CallStarted", (summary: LiveCallSummary) => {
      setActiveCalls(prev => [...prev, summary]);
    });

    newConnection.on("CallEnded", ({ sessionId }: { sessionId: string }) => {
      setActiveCalls(prev => 
        prev.map(c => c.sessionId === sessionId 
          ? { ...c, status: "Ended" as const, endedAt: new Date().toISOString() } 
          : c
        )
      );
    });

    newConnection.on("CallHealthUpdated", (summary: LiveCallSummary) => {
      setActiveCalls(prev => 
        prev.map(c => c.sessionId === summary.sessionId ? summary : c)
      );
      if (selectedCall?.sessionId === summary.sessionId) {
        setSelectedCall(summary);
      }
    });

    newConnection.on("CallDetails", (summary: LiveCallSummary) => {
      setSelectedCall(summary);
    });

    newConnection.start()
      .then(() => setIsConnected(true))
      .catch(err => console.error("SignalR Connection Error: ", err));

    setConnection(newConnection);

    return () => {
      newConnection.stop();
    };
  }, [apiBaseUrl]);

  const selectCall = useCallback(async (sessionId: string) => {
    if (connection && isConnected) {
      await connection.invoke("GetCallDetails", sessionId);
      await connection.invoke("SubscribeToSession", sessionId);
    }
  }, [connection, isConnected]);

  const deselectCall = useCallback(async () => {
    if (connection && isConnected && selectedCall) {
      await connection.invoke("UnsubscribeFromSession", selectedCall.sessionId);
      setSelectedCall(null);
    }
  }, [connection, isConnected, selectedCall]);

  return {
    activeCalls,
    selectedCall,
    isConnected,
    selectCall,
    deselectCall,
  };
}
```

## Health Metric Thresholds

Suggested thresholds for visual indicators:

| Metric | Green | Amber | Red |
|--------|-------|-------|-----|
| Customer Sentiment | > 0.3 | -0.3 to 0.3 | < -0.3 |
| Agent Sentiment | > 0.3 | -0.3 to 0.3 | < -0.3 |
| Task Adherence | > 0.7 | 0.4 to 0.7 | < 0.4 |
| Escalation Risk | < 0.3 | 0.3 to 0.6 | > 0.6 |

## Notes

- Health metrics are currently using a stub implementation with simple keyword-based analysis.
- The `duration` field is a computed property representing the time since call start.
- All timestamps are in UTC (ISO 8601 format).
- The SignalR hub uses the default JSON protocol.
