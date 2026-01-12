"use client";

import {
  HubConnectionBuilder,
  HubConnection,
  LogLevel,
} from "@microsoft/signalr";
import { useState, useEffect, useCallback, useRef } from "react";
import type { LiveCallSummary } from "@/lib/operator-types";

export interface UseOperatorDashboardOptions {
  apiBaseUrl: string;
}

export interface UseOperatorDashboardResult {
  activeCalls: LiveCallSummary[];
  selectedCall: LiveCallSummary | null;
  isConnected: boolean;
  connectionError: string | null;
  selectCall: (sessionId: string) => Promise<void>;
  deselectCall: () => Promise<void>;
  refreshCalls: () => Promise<void>;
}

async function fetchActiveCalls(
  apiBaseUrl: string,
  setActiveCalls: React.Dispatch<React.SetStateAction<LiveCallSummary[]>>
) {
  try {
    const response = await fetch(`${apiBaseUrl}/api/operator/calls/active`);
    if (response.ok) {
      const calls: LiveCallSummary[] = await response.json();
      setActiveCalls(calls);
    }
  } catch (error) {
    console.error("Failed to fetch active calls:", error);
  }
}

export function useOperatorDashboard({
  apiBaseUrl,
}: UseOperatorDashboardOptions): UseOperatorDashboardResult {
  const [connection, setConnection] = useState<HubConnection | null>(null);
  const [activeCalls, setActiveCalls] = useState<LiveCallSummary[]>([]);
  const [selectedCall, setSelectedCall] = useState<LiveCallSummary | null>(
    null
  );
  const [isConnected, setIsConnected] = useState(false);
  const [connectionError, setConnectionError] = useState<string | null>(null);
  const previousSessionRef = useRef<string | null>(null);
  const apiBaseUrlRef = useRef(apiBaseUrl);
  apiBaseUrlRef.current = apiBaseUrl;

  // Fetch active calls via REST API
  const refreshCalls = useCallback(async () => {
    await fetchActiveCalls(apiBaseUrlRef.current, setActiveCalls);
  }, []);

  useEffect(() => {
    let mounted = true;

    const newConnection = new HubConnectionBuilder()
      .withUrl(`${apiBaseUrl}/operatorHub`)
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Information)
      .build();

    // Handle CallStarted event
    newConnection.on("CallStarted", (summary: LiveCallSummary) => {
      if (!mounted) return;
      setActiveCalls((prev) => {
        // Avoid duplicates
        if (prev.some((c) => c.sessionId === summary.sessionId)) {
          return prev;
        }
        return [...prev, summary];
      });
    });

    // Handle CallEnded event - keep in list but mark as ended for visibility
    newConnection.on("CallEnded", ({ sessionId }: { sessionId: string }) => {
      if (!mounted) return;
      setActiveCalls((prev) =>
        prev.map((c) =>
          c.sessionId === sessionId
            ? {
                ...c,
                status: "Ended" as const,
                endedAt: new Date().toISOString(),
              }
            : c
        )
      );
    });

    // Handle CallHealthUpdated event
    newConnection.on("CallHealthUpdated", (summary: LiveCallSummary) => {
      if (!mounted) return;
      setActiveCalls((prev) =>
        prev.map((c) => (c.sessionId === summary.sessionId ? summary : c))
      );
      setSelectedCall((prev) =>
        prev?.sessionId === summary.sessionId ? summary : prev
      );
    });

    // Handle CallDetails event (response to GetCallDetails or subscription)
    newConnection.on("CallDetails", (summary: LiveCallSummary) => {
      if (!mounted) return;
      setSelectedCall(summary);
    });

    // Handle CallNotFound event
    newConnection.on("CallNotFound", ({ sessionId }: { sessionId: string }) => {
      if (!mounted) return;
      console.warn(`Call not found: ${sessionId}`);
      setSelectedCall(null);
    });

    // Handle reconnection
    newConnection.onreconnected(() => {
      if (!mounted) return;
      setIsConnected(true);
      setConnectionError(null);
      // Refresh the calls list on reconnection
      refreshCalls();
    });

    newConnection.onreconnecting((error) => {
      if (!mounted) return;
      setIsConnected(false);
      setConnectionError(`Reconnecting: ${error?.message || "Connection lost"}`);
    });

    newConnection.onclose((error) => {
      if (!mounted) return;
      setIsConnected(false);
      if (error) {
        setConnectionError(`Connection closed: ${error.message}`);
      }
    });

    // Start the connection
    newConnection
      .start()
      .then(() => {
        if (!mounted) return;
        setIsConnected(true);
        setConnectionError(null);
        // Initial fetch of active calls
        refreshCalls();
      })
      .catch((err) => {
        if (!mounted) return;
        console.error("SignalR Connection Error:", err);
        setConnectionError(
          `Failed to connect: ${err instanceof Error ? err.message : String(err)}`
        );
      });

    setConnection(newConnection);

    return () => {
      mounted = false;
      newConnection.stop();
    };
  }, [apiBaseUrl, refreshCalls]);

  const selectCall = useCallback(
    async (sessionId: string) => {
      if (connection && isConnected) {
        // Unsubscribe from previous session if any
        if (previousSessionRef.current && previousSessionRef.current !== sessionId) {
          try {
            await connection.invoke(
              "UnsubscribeFromSession",
              previousSessionRef.current
            );
          } catch (error) {
            console.error("Error unsubscribing from previous session:", error);
          }
        }

        try {
          await connection.invoke("GetCallDetails", sessionId);
          await connection.invoke("SubscribeToSession", sessionId);
          previousSessionRef.current = sessionId;
        } catch (error) {
          console.error("Error selecting call:", error);
        }
      }
    },
    [connection, isConnected]
  );

  const deselectCall = useCallback(async () => {
    if (connection && isConnected && previousSessionRef.current) {
      try {
        await connection.invoke(
          "UnsubscribeFromSession",
          previousSessionRef.current
        );
      } catch (error) {
        console.error("Error unsubscribing from session:", error);
      }
      previousSessionRef.current = null;
      setSelectedCall(null);
    }
  }, [connection, isConnected]);

  return {
    activeCalls,
    selectedCall,
    isConnected,
    connectionError,
    selectCall,
    deselectCall,
    refreshCalls,
  };
}
