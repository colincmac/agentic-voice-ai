"use client";

import { useState, useMemo } from "react";
import { useOperatorDashboard } from "@/hooks/useOperatorDashboard";
import {
  type LiveCallSummary,
  type LiveParticipantSummary,
  getSentimentColor,
  getAdherenceColor,
  getEscalationRiskColor,
  parseDurationToSeconds,
} from "@/lib/operator-types";

const API_BASE_URL =
  process.env.NEXT_PUBLIC_VOICE_AGENT_API_URL || "http://localhost:5000";

type SortField = "duration" | "escalationRisk" | null;
type SortDirection = "asc" | "desc";

export default function OperatorCallsPage() {
  const {
    activeCalls,
    selectedCall,
    isConnected,
    connectionError,
    selectCall,
    deselectCall,
    refreshCalls,
  } = useOperatorDashboard({ apiBaseUrl: API_BASE_URL });

  const [sortField, setSortField] = useState<SortField>(null);
  const [sortDirection, setSortDirection] = useState<SortDirection>("desc");

  // Sort calls based on current sort settings
  const sortedCalls = useMemo(() => {
    if (!sortField) return activeCalls;

    return [...activeCalls].sort((a, b) => {
      let comparison = 0;

      if (sortField === "duration") {
        comparison =
          parseDurationToSeconds(a.duration) -
          parseDurationToSeconds(b.duration);
      } else if (sortField === "escalationRisk") {
        const aRisk = a.escalationRiskScore ?? 0;
        const bRisk = b.escalationRiskScore ?? 0;
        comparison = aRisk - bRisk;
      }

      return sortDirection === "asc" ? comparison : -comparison;
    });
  }, [activeCalls, sortField, sortDirection]);

  // Memoize active calls count to avoid re-filtering on every render
  const activeCallsCount = useMemo(
    () => activeCalls.filter((c) => c.status !== "Ended").length,
    [activeCalls]
  );

  const handleSort = (field: SortField) => {
    if (sortField === field) {
      setSortDirection((prev) => (prev === "asc" ? "desc" : "asc"));
    } else {
      setSortField(field);
      setSortDirection("desc");
    }
  };

  const getSortIndicator = (field: SortField) => {
    if (sortField !== field) return "";
    return sortDirection === "asc" ? " ▲" : " ▼";
  };

  return (
    <div className="min-h-screen bg-gray-50 p-4">
      <div className="max-w-7xl mx-auto">
        {/* Header */}
        <div className="mb-6 flex items-center justify-between">
          <div>
            <h1 className="text-2xl font-bold text-gray-900">
              Operator Dashboard
            </h1>
            <p className="text-sm text-gray-500">
              Live call monitoring and analytics
            </p>
          </div>
          <div className="flex items-center gap-4">
            <ConnectionStatus
              isConnected={isConnected}
              error={connectionError}
            />
            <button
              onClick={refreshCalls}
              className="px-3 py-1.5 text-sm bg-blue-600 text-white rounded hover:bg-blue-700 transition-colors"
            >
              Refresh
            </button>
          </div>
        </div>

        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          {/* Calls Table */}
          <div className="lg:col-span-2">
            <div className="bg-white rounded-lg shadow overflow-hidden">
              <div className="p-4 border-b border-gray-200">
                <h2 className="text-lg font-semibold text-gray-800">
                  Active Calls ({activeCallsCount})
                </h2>
              </div>
              <div className="overflow-x-auto">
                <table className="w-full">
                  <thead className="bg-gray-50">
                    <tr>
                      <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Customer
                      </th>
                      <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Agent(s)
                      </th>
                      <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Status
                      </th>
                      <th
                        className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider cursor-pointer hover:bg-gray-100"
                        onClick={() => handleSort("duration")}
                        role="button"
                        aria-label="Sort by duration"
                        tabIndex={0}
                        onKeyDown={(e) => e.key === "Enter" && handleSort("duration")}
                      >
                        Duration{getSortIndicator("duration")}
                      </th>
                      <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Sentiment
                      </th>
                      <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Adherence
                      </th>
                      <th
                        className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider cursor-pointer hover:bg-gray-100"
                        onClick={() => handleSort("escalationRisk")}
                        role="button"
                        aria-label="Sort by escalation risk"
                        tabIndex={0}
                        onKeyDown={(e) => e.key === "Enter" && handleSort("escalationRisk")}
                      >
                        Risk{getSortIndicator("escalationRisk")}
                      </th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-gray-200">
                    {sortedCalls.length === 0 ? (
                      <tr>
                        <td
                          colSpan={7}
                          className="px-4 py-8 text-center text-gray-500"
                        >
                          No active calls
                        </td>
                      </tr>
                    ) : (
                      sortedCalls.map((call) => (
                        <CallRow
                          key={call.sessionId}
                          call={call}
                          isSelected={selectedCall?.sessionId === call.sessionId}
                          onSelect={() => selectCall(call.sessionId)}
                        />
                      ))
                    )}
                  </tbody>
                </table>
              </div>
            </div>
          </div>

          {/* Detail Panel */}
          <div className="lg:col-span-1">
            <CallDetailPanel call={selectedCall} onClose={deselectCall} />
          </div>
        </div>
      </div>
    </div>
  );
}

function ConnectionStatus({
  isConnected,
  error,
}: {
  isConnected: boolean;
  error: string | null;
}) {
  return (
    <div className="flex items-center gap-2">
      <div
        className={`w-2.5 h-2.5 rounded-full ${
          isConnected ? "bg-green-500" : "bg-red-500"
        }`}
      />
      <span className="text-sm text-gray-600">
        {isConnected ? "Connected" : error || "Disconnected"}
      </span>
    </div>
  );
}

function CallRow({
  call,
  isSelected,
  onSelect,
}: {
  call: LiveCallSummary;
  isSelected: boolean;
  onSelect: () => void;
}) {
  const customer = call.participants.find((p) => p.participantType === "Customer");
  const agents = call.participants.filter(
    (p) => p.participantType === "Agent" || p.participantType === "AIAgent"
  );

  return (
    <tr
      className={`cursor-pointer hover:bg-gray-50 transition-colors ${
        isSelected ? "bg-blue-50" : ""
      } ${call.status === "Ended" ? "opacity-60" : ""}`}
      onClick={onSelect}
    >
      <td className="px-4 py-3">
        <div className="text-sm font-medium text-gray-900">
          {customer?.displayName || customer?.participantId || "Unknown"}
        </div>
        <div className="text-xs text-gray-500">
          {customer?.channelType || "Unknown Channel"}
        </div>
      </td>
      <td className="px-4 py-3">
        <div className="flex flex-wrap gap-1">
          {agents.map((agent) => (
            <span
              key={agent.participantId}
              className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${
                agent.participantType === "AIAgent"
                  ? "bg-purple-100 text-purple-800"
                  : "bg-blue-100 text-blue-800"
              }`}
            >
              {agent.displayName || agent.participantId}
            </span>
          ))}
        </div>
      </td>
      <td className="px-4 py-3">
        <StatusBadge status={call.status} />
      </td>
      <td className="px-4 py-3 text-sm text-gray-900 font-mono">
        {call.duration}
      </td>
      <td className="px-4 py-3">
        <MetricBadge
          value={call.customerSentiment}
          color={getSentimentColor(call.customerSentiment)}
          format={(v) => (v >= 0 ? `+${v.toFixed(1)}` : v.toFixed(1))}
        />
      </td>
      <td className="px-4 py-3">
        <MetricBadge
          value={call.taskAdherenceScore}
          color={getAdherenceColor(call.taskAdherenceScore)}
          format={(v) => `${(v * 100).toFixed(0)}%`}
        />
      </td>
      <td className="px-4 py-3">
        <MetricBadge
          value={call.escalationRiskScore}
          color={getEscalationRiskColor(call.escalationRiskScore)}
          format={(v) => `${(v * 100).toFixed(0)}%`}
        />
      </td>
    </tr>
  );
}

function StatusBadge({ status }: { status: string }) {
  const colorMap: Record<string, string> = {
    Active: "bg-green-100 text-green-800",
    Connecting: "bg-yellow-100 text-yellow-800",
    OnHold: "bg-orange-100 text-orange-800",
    Ended: "bg-gray-100 text-gray-800",
    Failed: "bg-red-100 text-red-800",
  };

  return (
    <span
      className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${
        colorMap[status] || "bg-gray-100 text-gray-800"
      }`}
    >
      {status}
    </span>
  );
}

function MetricBadge({
  value,
  color,
  format,
}: {
  value: number | null;
  color: "green" | "amber" | "red" | "gray";
  format: (v: number) => string;
}) {
  const colorMap = {
    green: "bg-green-100 text-green-800",
    amber: "bg-yellow-100 text-yellow-800",
    red: "bg-red-100 text-red-800",
    gray: "bg-gray-100 text-gray-500",
  };

  return (
    <span
      className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${colorMap[color]}`}
    >
      {value !== null ? format(value) : "—"}
    </span>
  );
}

function CallDetailPanel({
  call,
  onClose,
}: {
  call: LiveCallSummary | null;
  onClose: () => void;
}) {
  if (!call) {
    return (
      <div className="bg-white rounded-lg shadow p-6 text-center text-gray-500">
        <p>Select a call to view details</p>
      </div>
    );
  }

  return (
    <div className="bg-white rounded-lg shadow">
      <div className="p-4 border-b border-gray-200 flex items-center justify-between">
        <h3 className="text-lg font-semibold text-gray-800">Call Details</h3>
        <button
          onClick={onClose}
          className="text-gray-400 hover:text-gray-600"
          aria-label="Close call details"
        >
          ✕
        </button>
      </div>

      <div className="p-4 space-y-4">
        {/* Call Info */}
        <div>
          <div className="text-xs text-gray-500 uppercase tracking-wider mb-1">
            Session ID
          </div>
          <div className="text-sm font-mono text-gray-900 break-all">
            {call.sessionId}
          </div>
        </div>

        {/* Status & Duration */}
        <div className="grid grid-cols-2 gap-4">
          <div>
            <div className="text-xs text-gray-500 uppercase tracking-wider mb-1">
              Status
            </div>
            <StatusBadge status={call.status} />
          </div>
          <div>
            <div className="text-xs text-gray-500 uppercase tracking-wider mb-1">
              Duration
            </div>
            <div className="text-sm font-mono text-gray-900">
              {call.duration}
            </div>
          </div>
        </div>

        {/* Health Metrics */}
        <div>
          <div className="text-xs text-gray-500 uppercase tracking-wider mb-2">
            Health Metrics
          </div>
          <div className="space-y-2">
            <HealthMetricBar
              label="Customer Sentiment"
              value={call.customerSentiment}
              min={-1}
              max={1}
              color={getSentimentColor(call.customerSentiment)}
            />
            <HealthMetricBar
              label="Agent Sentiment"
              value={call.agentSentiment}
              min={-1}
              max={1}
              color={getSentimentColor(call.agentSentiment)}
            />
            <HealthMetricBar
              label="Task Adherence"
              value={call.taskAdherenceScore}
              min={0}
              max={1}
              color={getAdherenceColor(call.taskAdherenceScore)}
            />
            <HealthMetricBar
              label="Escalation Risk"
              value={call.escalationRiskScore}
              min={0}
              max={1}
              color={getEscalationRiskColor(call.escalationRiskScore)}
            />
          </div>
        </div>

        {/* Active Tasks */}
        {call.activeTasks.length > 0 && (
          <div>
            <div className="text-xs text-gray-500 uppercase tracking-wider mb-2">
              Active Tasks
            </div>
            <div className="flex flex-wrap gap-1">
              {call.activeTasks.map((task, index) => (
                <span
                  key={index}
                  className="inline-flex items-center px-2 py-0.5 rounded text-xs bg-indigo-100 text-indigo-800"
                >
                  {task}
                </span>
              ))}
            </div>
          </div>
        )}

        {/* Latest Utterance */}
        {/* Latest Utterance - React JSX automatically escapes string content, preventing XSS */}
        {call.latestUtteranceSummary && (
          <div>
            <div className="text-xs text-gray-500 uppercase tracking-wider mb-1">
              Latest Utterance
            </div>
            <div className="text-sm text-gray-700 italic bg-gray-50 p-2 rounded">
              &quot;{call.latestUtteranceSummary}&quot;
            </div>
          </div>
        )}

        {/* Participants */}
        <div>
          <div className="text-xs text-gray-500 uppercase tracking-wider mb-2">
            Participants ({call.participants.length})
          </div>
          <div className="space-y-2">
            {call.participants.map((participant) => (
              <ParticipantCard
                key={participant.participantId}
                participant={participant}
              />
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

function HealthMetricBar({
  label,
  value,
  min,
  max,
  color,
}: {
  label: string;
  value: number | null;
  min: number;
  max: number;
  color: "green" | "amber" | "red" | "gray";
}) {
  const colorMap = {
    green: "bg-green-500",
    amber: "bg-yellow-500",
    red: "bg-red-500",
    gray: "bg-gray-300",
  };

  // Normalize value to 0-100 percentage
  const normalizedValue =
    value !== null ? ((value - min) / (max - min)) * 100 : 0;

  return (
    <div>
      <div className="flex justify-between text-xs mb-1">
        <span className="text-gray-600">{label}</span>
        <span className="text-gray-900 font-medium">
          {value !== null ? value.toFixed(2) : "—"}
        </span>
      </div>
      <div className="h-2 bg-gray-200 rounded-full overflow-hidden">
        <div
          className={`h-full ${colorMap[color]} transition-all duration-300`}
          style={{ width: `${Math.max(0, Math.min(100, normalizedValue))}%` }}
        />
      </div>
    </div>
  );
}

function ParticipantCard({
  participant,
}: {
  participant: LiveParticipantSummary;
}) {
  const typeColorMap: Record<string, string> = {
    Customer: "border-l-blue-500",
    Agent: "border-l-green-500",
    AIAgent: "border-l-purple-500",
    Supervisor: "border-l-orange-500",
    System: "border-l-gray-500",
  };

  return (
    <div
      className={`border-l-4 ${
        typeColorMap[participant.participantType] || "border-l-gray-500"
      } bg-gray-50 p-2 rounded-r`}
    >
      <div className="flex items-center justify-between">
        <div>
          <div className="text-sm font-medium text-gray-900">
            {participant.displayName || participant.participantId}
          </div>
          <div className="text-xs text-gray-500">
            {participant.participantType} • {participant.channelType}
          </div>
        </div>
        <div className="flex items-center gap-1">
          {participant.isMuted && (
            <span className="text-xs bg-gray-200 text-gray-600 px-1 rounded">
              Muted
            </span>
          )}
          {participant.isOnHold && (
            <span className="text-xs bg-yellow-200 text-yellow-800 px-1 rounded">
              Hold
            </span>
          )}
          {!participant.isConnected && (
            <span className="text-xs bg-red-200 text-red-800 px-1 rounded">
              Disconnected
            </span>
          )}
        </div>
      </div>
    </div>
  );
}
