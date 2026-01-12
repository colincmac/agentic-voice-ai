"use client";

import { AncestryReport, AncestryReportToolResult } from "@/lib/types";

export interface AncestryReportCardProps {
  result: AncestryReportToolResult;
  themeColor: string;
}

// Helper to format dates
function formatDate(dateString?: string): string {
  if (!dateString) return "Unknown";
  try {
    return new Date(dateString).toLocaleDateString("en-US", {
      year: "numeric",
      month: "long",
      day: "numeric",
    });
  } catch {
    return dateString;
  }
}

// Section component for list items
function ListSection({
  title,
  items,
  emptyText,
}: {
  title: string;
  items: string[];
  emptyText?: string;
}) {
  if (items.length === 0 && !emptyText) return null;

  return (
    <div className="mb-4">
      <h4 className="text-white/80 text-sm font-medium mb-2">{title}</h4>
      {items.length > 0 ? (
        <ul className="space-y-1">
          {items.map((item, i) => (
            <li
              key={i}
              className="text-white/90 text-sm pl-3 border-l-2 border-white/30"
            >
              {item}
            </li>
          ))}
        </ul>
      ) : (
        <p className="text-white/60 text-sm italic">{emptyText}</p>
      )}
    </div>
  );
}

// Render the report content
function ReportContent({
  report,
  themeColor,
}: {
  report: AncestryReport;
  themeColor: string;
}) {
  return (
    <div
      style={{ backgroundColor: themeColor }}
      className="rounded-2xl shadow-xl max-w-lg w-full"
    >
      <div className="bg-white/20 backdrop-blur-md p-6 rounded-2xl">
        {/* Header */}
        <div className="mb-4">
          <div className="flex items-center gap-2 mb-2">
            <span className="text-2xl">📜</span>
            <h3 className="text-xl font-bold text-white">
              {report.title || `Ancestry Report: ${report.focusPersonName}`}
            </h3>
          </div>
          <p className="text-white/70 text-sm">
            Focus: <strong>{report.focusPersonName}</strong>
          </p>
          <p className="text-white/50 text-xs mt-1">
            Generated: {formatDate(report.createdAt)}
          </p>
        </div>

        {/* Overview */}
        {report.overview && (
          <div className="mb-4">
            <h4 className="text-white/80 text-sm font-medium mb-1">Overview</h4>
            <p className="text-white/90 text-sm leading-relaxed bg-white/10 p-3 rounded-lg">
              {report.overview}
            </p>
          </div>
        )}

        {/* Family Tree Narrative */}
        {report.familyTreeNarrative && (
          <div className="mb-4">
            <h4 className="text-white/80 text-sm font-medium mb-1">
              Family Tree Narrative
            </h4>
            <div className="text-white/90 text-sm leading-relaxed bg-white/10 p-3 rounded-lg whitespace-pre-line">
              {report.familyTreeNarrative}
            </div>
          </div>
        )}

        {/* Key Events */}
        <ListSection
          title="🗓️ Key Life Events"
          items={report.keyEvents}
          emptyText="No key events documented yet."
        />

        {/* Notable Relationships */}
        <ListSection
          title="👥 Notable Relationships"
          items={report.notableRelationships}
        />

        {/* Data Gaps */}
        {report.dataGaps.length > 0 && (
          <div className="mb-4">
            <h4 className="text-white/80 text-sm font-medium mb-2">
              ❓ Data Gaps
            </h4>
            <div className="bg-yellow-400/20 rounded-lg p-3">
              <ul className="space-y-1">
                {report.dataGaps.map((gap, i) => (
                  <li key={i} className="text-yellow-200 text-sm">
                    • {gap}
                  </li>
                ))}
              </ul>
            </div>
          </div>
        )}

        {/* Research Suggestions */}
        {report.researchSuggestions.length > 0 && (
          <div className="mb-4">
            <h4 className="text-white/80 text-sm font-medium mb-2">
              🔍 Research Suggestions
            </h4>
            <div className="bg-blue-400/20 rounded-lg p-3">
              <ul className="space-y-1">
                {report.researchSuggestions.map((suggestion, i) => (
                  <li key={i} className="text-blue-200 text-sm">
                    • {suggestion}
                  </li>
                ))}
              </ul>
            </div>
          </div>
        )}

        {/* Metadata Footer */}
        <div className="mt-4 pt-3 border-t border-white/20">
          <div className="flex flex-wrap gap-3 text-xs text-white/50">
            {report.linkedEntityIds.length > 0 && (
              <span>
                📊 {report.linkedEntityIds.length} linked entities
              </span>
            )}
            {report.sourceContentIds.length > 0 && (
              <span>
                📄 {report.sourceContentIds.length} source documents
              </span>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

export function AncestryReportCard({
  result,
  themeColor,
}: AncestryReportCardProps) {
  // Handle failure case
  if (!result.success || !result.report) {
    return (
      <div
        style={{ backgroundColor: themeColor }}
        className="rounded-2xl shadow-xl max-w-lg w-full"
      >
        <div className="bg-white/20 backdrop-blur-md p-6 rounded-2xl">
          <div className="flex items-center gap-2 mb-2">
            <span className="text-2xl">⚠️</span>
            <h3 className="text-xl font-bold text-white">
              Report Generation Failed
            </h3>
          </div>
          <p className="text-white/90 text-sm">{result.message}</p>
        </div>
      </div>
    );
  }

  return <ReportContent report={result.report} themeColor={themeColor} />;
}
