"use client";

import { useState, useEffect, useRef, useCallback } from "react";

export interface CallCardProps {
  themeColor: string;
  callId?: string;
  callTarget?: string;
}

interface AcsTokenResponse {
  userId: string;
  token: string;
  expiresOn: string;
}

// Phone icon SVG component
function PhoneIcon({ className }: { className?: string }) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 24 24"
      fill="currentColor"
      className={className}
    >
      <path
        fillRule="evenodd"
        d="M1.5 4.5a3 3 0 013-3h1.372c.86 0 1.61.586 1.819 1.42l1.105 4.423a1.875 1.875 0 01-.694 1.955l-1.293.97c-.135.101-.164.249-.126.352a11.285 11.285 0 006.697 6.697c.103.038.25.009.352-.126l.97-1.293a1.875 1.875 0 011.955-.694l4.423 1.105c.834.209 1.42.959 1.42 1.82V19.5a3 3 0 01-3 3h-2.25C8.552 22.5 1.5 15.448 1.5 6.75V4.5z"
        clipRule="evenodd"
      />
    </svg>
  );
}

// Video camera icon SVG component
function VideoIcon({ className }: { className?: string }) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 24 24"
      fill="currentColor"
      className={className}
    >
      <path d="M4.5 4.5a3 3 0 00-3 3v9a3 3 0 003 3h8.25a3 3 0 003-3v-9a3 3 0 00-3-3H4.5zM19.94 18.75l-2.69-2.69V7.94l2.69-2.69c.944-.945 2.56-.276 2.56 1.06v11.38c0 1.336-1.616 2.005-2.56 1.06z" />
    </svg>
  );
}

// Type definitions for dynamically loaded ACS modules
type CallClient = import("@azure/communication-calling").CallClient;
type CallAgent = import("@azure/communication-calling").CallAgent;
type DeviceManager = import("@azure/communication-calling").DeviceManager;
type Call = import("@azure/communication-calling").Call;
type LocalVideoStream = import("@azure/communication-calling").LocalVideoStream;
type VideoStreamRenderer = import("@azure/communication-calling").VideoStreamRenderer;
type VideoStreamRendererView = import("@azure/communication-calling").VideoStreamRendererView;

// Map status to indicator class for cleaner rendering
const statusIndicatorClasses: Record<string, string> = {
  connected: "bg-green-400 animate-pulse",
  calling: "bg-yellow-400 animate-pulse",
  ready: "bg-blue-400",
  error: "bg-red-400",
  initializing: "bg-gray-400",
  ended: "bg-gray-400",
};

// Generate a UUID with fallback for non-secure contexts
function generateUUID(): string {
  if (typeof crypto !== "undefined" && crypto.randomUUID) {
    return crypto.randomUUID();
  }
  // Fallback for non-secure contexts
  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    const v = c === "x" ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}

export function CallCard({ themeColor, callId, callTarget }: CallCardProps) {
  const [status, setStatus] = useState<
    "initializing" | "ready" | "calling" | "connected" | "ended" | "error"
  >("initializing");
  const [errorMessage, setErrorMessage] = useState<string>("");
  const [hasPermissions, setHasPermissions] = useState(false);

  const callClientRef = useRef<CallClient | null>(null);
  const callAgentRef = useRef<CallAgent | null>(null);
  const deviceManagerRef = useRef<DeviceManager | null>(null);
  const currentCallRef = useRef<Call | null>(null);
  const localVideoStreamRef = useRef<LocalVideoStream | null>(null);
  const localVideoRendererRef = useRef<VideoStreamRenderer | null>(null);
  const localVideoViewRef = useRef<VideoStreamRendererView | null>(null);
  const localVideoContainerRef = useRef<HTMLDivElement | null>(null);

  // Initialize ACS client and request permissions
  useEffect(() => {
    const initialize = async () => {
      try {
        // Dynamic import of ACS modules (only in browser)
        const [callingModule, commonModule] = await Promise.all([
          import("@azure/communication-calling"),
          import("@azure/communication-common"),
        ]);
        const { CallClient } = callingModule;
        const { AzureCommunicationTokenCredential } = commonModule;

        // Fetch ACS token from backend
        const response = await fetch("/api/acs-token", { method: "POST" });
        if (!response.ok) {
          throw new Error("Failed to get ACS token");
        }
        const tokenData: AcsTokenResponse = await response.json();

        // Create call client
        const callClient = new CallClient();
        callClientRef.current = callClient;

        // Create token credential
        const tokenCredential = new AzureCommunicationTokenCredential(
          tokenData.token
        );

        // Create call agent
        const callAgent = await callClient.createCallAgent(tokenCredential, {
          displayName: "Family AI User",
        });
        callAgentRef.current = callAgent;

        // Get device manager
        const deviceManager = await callClient.getDeviceManager();
        deviceManagerRef.current = deviceManager;

        // Request permissions
        await deviceManager.askDevicePermission({ audio: true, video: true });
        setHasPermissions(true);

        setStatus("ready");
      } catch (error) {
        console.error("Failed to initialize ACS:", error);
        setErrorMessage(
          error instanceof Error ? error.message : "Failed to initialize calling"
        );
        setStatus("error");
      }
    };

    initialize();

    // Cleanup on unmount
    return () => {
      if (currentCallRef.current) {
        currentCallRef.current.hangUp().catch(console.error);
      }
      if (localVideoViewRef.current) {
        localVideoViewRef.current.dispose();
      }
      if (localVideoRendererRef.current) {
        localVideoRendererRef.current.dispose();
      }
      if (callAgentRef.current) {
        callAgentRef.current.dispose();
      }
    };
  }, []);

  const startLocalVideo = useCallback(async () => {
    if (!deviceManagerRef.current || !localVideoContainerRef.current) return;

    try {
      // Dynamic import of ACS calling module
      const { LocalVideoStream, VideoStreamRenderer } = await import(
        "@azure/communication-calling"
      );

      const cameras = await deviceManagerRef.current.getCameras();
      if (cameras.length > 0) {
        const localVideoStream = new LocalVideoStream(cameras[0]);
        localVideoStreamRef.current = localVideoStream;

        const renderer = new VideoStreamRenderer(localVideoStream);
        localVideoRendererRef.current = renderer;

        const view = await renderer.createView({ scalingMode: "Crop" });
        localVideoViewRef.current = view;

        // Clear existing children safely using replaceChildren()
        localVideoContainerRef.current.replaceChildren(view.target);
      }
    } catch (error) {
      console.error("Failed to start local video:", error);
    }
  }, []);

  const startCall = async () => {
    if (!callAgentRef.current || !deviceManagerRef.current) {
      setErrorMessage("Call client not initialized");
      return;
    }

    try {
      setStatus("calling");

      // Start local video preview
      await startLocalVideo();

      // For demo purposes, we start a call to a placeholder group
      // In production, you would call a specific user or join a Teams meeting
      const groupId = callId || generateUUID();

      const callOptions: {
        videoOptions?: { localVideoStreams: LocalVideoStream[] };
      } = {};
      if (localVideoStreamRef.current) {
        callOptions.videoOptions = {
          localVideoStreams: [localVideoStreamRef.current],
        };
      }

      const call = callAgentRef.current.join({ groupId }, callOptions);
      currentCallRef.current = call;

      // Subscribe to call state changes
      call.on("stateChanged", () => {
        switch (call.state) {
          case "Connected":
            setStatus("connected");
            break;
          case "Disconnected":
          case "Disconnecting":
            setStatus("ended");
            break;
        }
      });
    } catch (error) {
      console.error("Failed to start call:", error);
      setErrorMessage(
        error instanceof Error ? error.message : "Failed to start call"
      );
      setStatus("error");
    }
  };

  const hangUp = async () => {
    if (currentCallRef.current) {
      try {
        await currentCallRef.current.hangUp();
        currentCallRef.current = null;
        setStatus("ended");
      } catch (error) {
        console.error("Failed to hang up:", error);
      }
    }

    // Clean up video
    if (localVideoViewRef.current) {
      localVideoViewRef.current.dispose();
      localVideoViewRef.current = null;
    }
    if (localVideoRendererRef.current) {
      localVideoRendererRef.current.dispose();
      localVideoRendererRef.current = null;
    }
  };

  const resetCall = () => {
    setStatus("ready");
    setErrorMessage("");
  };

  return (
    <div
      style={{ backgroundColor: themeColor }}
      className="rounded-2xl shadow-xl max-w-md w-full mt-6"
    >
      <div className="bg-white/20 backdrop-blur-md p-6 w-full rounded-2xl">
        {/* Header */}
        <div className="flex items-center justify-between mb-4">
          <div className="flex items-center gap-3">
            <div className="bg-white/30 p-2 rounded-full">
              <VideoIcon className="w-6 h-6 text-white" />
            </div>
            <div>
              <h3 className="text-xl font-bold text-white">Video Call</h3>
              <p className="text-white/80 text-sm">
                {callTarget || "Family AI Call"}
              </p>
            </div>
          </div>
          {/* Status indicator */}
          <div className="flex items-center gap-2">
            <div
              className={`w-3 h-3 rounded-full ${statusIndicatorClasses[status] || "bg-gray-400"}`}
            />
            <span className="text-white/80 text-xs capitalize">{status}</span>
          </div>
        </div>

        {/* Video placeholder / preview */}
        <div className="relative bg-black/40 rounded-xl overflow-hidden aspect-video mb-4">
          <div
            ref={localVideoContainerRef}
            className="absolute inset-0 w-full h-full"
          />
          {status === "initializing" && (
            <div className="absolute inset-0 flex items-center justify-center">
              <div className="text-white/60 text-center">
                <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-white mx-auto mb-2" />
                <p className="text-sm">Initializing...</p>
              </div>
            </div>
          )}
          {status === "ready" && !hasPermissions && (
            <div className="absolute inset-0 flex items-center justify-center">
              <div className="text-white/60 text-center p-4">
                <VideoIcon className="w-12 h-12 mx-auto mb-2 opacity-50" />
                <p className="text-sm">Camera permissions required</p>
              </div>
            </div>
          )}
          {status === "ready" && hasPermissions && (
            <div className="absolute inset-0 flex items-center justify-center">
              <div className="text-white/60 text-center">
                <VideoIcon className="w-12 h-12 mx-auto mb-2 opacity-50" />
                <p className="text-sm">Ready to start call</p>
              </div>
            </div>
          )}
          {status === "ended" && (
            <div className="absolute inset-0 flex items-center justify-center">
              <div className="text-white text-center">
                <PhoneIcon className="w-12 h-12 mx-auto mb-2 opacity-50" />
                <p className="text-sm">Call ended</p>
              </div>
            </div>
          )}
          {status === "error" && (
            <div className="absolute inset-0 flex items-center justify-center">
              <div className="text-red-300 text-center p-4">
                <p className="text-sm">{errorMessage}</p>
              </div>
            </div>
          )}
        </div>

        {/* Call controls */}
        <div className="flex gap-3 justify-center">
          {(status === "ready" || status === "ended") && (
            <button
              onClick={status === "ended" ? resetCall : startCall}
              className="flex-1 max-w-[200px] px-6 py-3 rounded-xl bg-green-500 hover:bg-green-600 
                text-white font-bold shadow-lg transition-all hover:scale-105 active:scale-95
                flex items-center justify-center gap-2"
            >
              <PhoneIcon className="w-5 h-5" />
              {status === "ended" ? "New Call" : "Start Call"}
            </button>
          )}
          {(status === "calling" || status === "connected") && (
            <button
              onClick={hangUp}
              className="flex-1 max-w-[200px] px-6 py-3 rounded-xl bg-red-500 hover:bg-red-600 
                text-white font-bold shadow-lg transition-all hover:scale-105 active:scale-95
                flex items-center justify-center gap-2"
            >
              <PhoneIcon className="w-5 h-5" />
              Hang Up
            </button>
          )}
          {status === "error" && (
            <button
              onClick={resetCall}
              className="flex-1 max-w-[200px] px-6 py-3 rounded-xl bg-white/20 hover:bg-white/30 
                text-white font-bold shadow-lg transition-all hover:scale-105 active:scale-95"
            >
              Try Again
            </button>
          )}
          {status === "initializing" && (
            <div className="text-white/60 text-sm py-3">
              Setting up call client...
            </div>
          )}
        </div>

        {/* Call ID display */}
        {callId && (
          <div className="mt-4 pt-4 border-t border-white/20">
            <p className="text-white/60 text-xs text-center">
              Call ID: <span className="font-mono">{callId}</span>
            </p>
          </div>
        )}
      </div>
    </div>
  );
}
