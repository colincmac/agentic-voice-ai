/**
 * Types for the Operator Dashboard API.
 * These types match the backend DTOs from the VoiceAgent API.
 */

export type LiveCallStatus =
  | "Connecting"
  | "Active"
  | "OnHold"
  | "Ended"
  | "Failed";

export type ParticipantType =
  | "Customer"
  | "Agent"
  | "AIAgent"
  | "Supervisor"
  | "System";

export type ParticipantRole =
  | "Caller"
  | "Callee"
  | "Observer"
  | "Assistant"
  | "Moderator";

export type CommunicationChannelType =
  | "TeamsChatThread"
  | "Phone"
  | "ChatAIAgent"
  | "VoiceAIAgent"
  | "AcsUser"
  | "Unknown";

export interface LiveParticipantSummary {
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

export interface LiveCallSummary {
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

  // Computed (TimeSpan as string like "00:05:23")
  duration: string;
}

/**
 * Health metric thresholds for visual indicators.
 */
export const HEALTH_THRESHOLDS = {
  sentiment: {
    good: 0.3,
    warning: -0.3,
  },
  taskAdherence: {
    good: 0.7,
    warning: 0.4,
  },
  escalationRisk: {
    good: 0.3, // Lower is better
    warning: 0.6,
  },
} as const;

/**
 * Returns "green", "amber", or "red" based on sentiment value.
 */
export function getSentimentColor(
  sentiment: number | null
): "green" | "amber" | "red" | "gray" {
  if (sentiment === null) return "gray";
  if (sentiment > HEALTH_THRESHOLDS.sentiment.good) return "green";
  if (sentiment < HEALTH_THRESHOLDS.sentiment.warning) return "red";
  return "amber";
}

/**
 * Returns "green", "amber", or "red" based on task adherence score.
 */
export function getAdherenceColor(
  score: number | null
): "green" | "amber" | "red" | "gray" {
  if (score === null) return "gray";
  if (score > HEALTH_THRESHOLDS.taskAdherence.good) return "green";
  if (score < HEALTH_THRESHOLDS.taskAdherence.warning) return "red";
  return "amber";
}

/**
 * Returns "green", "amber", or "red" based on escalation risk score.
 * Note: For escalation risk, lower is better (green).
 */
export function getEscalationRiskColor(
  score: number | null
): "green" | "amber" | "red" | "gray" {
  if (score === null) return "gray";
  if (score < HEALTH_THRESHOLDS.escalationRisk.good) return "green";
  if (score > HEALTH_THRESHOLDS.escalationRisk.warning) return "red";
  return "amber";
}

/**
 * Safely parse an integer string, returning 0 for invalid input.
 */
function safeParseInt(value: string): number {
  const parsed = parseInt(value, 10);
  return Number.isNaN(parsed) ? 0 : parsed;
}

/**
 * Parse duration string to total seconds for sorting.
 */
export function parseDurationToSeconds(duration: string): number {
  const parts = duration.split(":");
  if (parts.length === 3) {
    const hours = safeParseInt(parts[0]);
    const minutes = safeParseInt(parts[1]);
    const seconds = safeParseInt(parts[2]);
    return hours * 3600 + minutes * 60 + seconds;
  }
  return 0;
}
