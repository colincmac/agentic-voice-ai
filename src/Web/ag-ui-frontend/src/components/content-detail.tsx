"use client";

import {
  FamilyContent,
  ProcessedContent,
  ContentMetadataSummary,
} from "@/lib/types";

export interface ContentDetailCardProps {
  content?: FamilyContent;
  processedContent?: ProcessedContent;
  metadata?: ContentMetadataSummary;
  themeColor: string;
  onClose?: () => void;
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

// Helper to format file size
function formatFileSize(bytes?: number): string {
  if (!bytes) return "Unknown";
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

// Status badge component
function StatusBadge({ status }: { status: string }) {
  const colors: Record<string, string> = {
    Pending: "bg-yellow-400/30 text-yellow-200",
    Processing: "bg-blue-400/30 text-blue-200",
    Completed: "bg-green-400/30 text-green-200",
    Failed: "bg-red-400/30 text-red-200",
  };

  return (
    <span
      className={`px-2 py-1 rounded-full text-xs font-medium ${colors[status] || "bg-gray-400/30 text-gray-200"}`}
    >
      {status}
    </span>
  );
}

// Entity chip component
function EntityChip({
  type,
  value,
  confidence,
}: {
  type: string;
  value: string;
  confidence: number;
}) {
  const typeColors: Record<string, string> = {
    Person: "bg-purple-400/30",
    Location: "bg-blue-400/30",
    Date: "bg-green-400/30",
    Event: "bg-orange-400/30",
  };

  return (
    <div
      className={`inline-flex items-center gap-1.5 px-2 py-1 rounded-lg text-xs text-white ${typeColors[type] || "bg-gray-400/30"}`}
    >
      <span className="font-medium">{value}</span>
      <span className="text-white/60">({Math.round(confidence * 100)}%)</span>
    </div>
  );
}

export function ContentDetailCard({
  content,
  processedContent,
  metadata,
  themeColor,
  onClose,
}: ContentDetailCardProps) {
  // If we only have metadata, show a simpler view
  if (!content && metadata) {
    return (
      <div
        style={{ backgroundColor: themeColor }}
        className="rounded-2xl shadow-xl max-w-lg w-full mt-6"
      >
        <div className="bg-white/20 backdrop-blur-md p-6 rounded-2xl">
          <div className="flex items-start justify-between mb-4">
            <div>
              <h3 className="text-xl font-bold text-white">
                {metadata.title || "Untitled Content"}
              </h3>
              <div className="flex items-center gap-2 mt-1">
                <span className="text-white/70 text-sm uppercase">
                  {metadata.contentType}
                </span>
                <StatusBadge status={metadata.status} />
              </div>
            </div>
            {onClose && (
              <button
                onClick={onClose}
                className="text-white/60 hover:text-white transition-colors p-1"
              >
                <svg
                  xmlns="http://www.w3.org/2000/svg"
                  className="h-5 w-5"
                  viewBox="0 0 20 20"
                  fill="currentColor"
                >
                  <path
                    fillRule="evenodd"
                    d="M4.293 4.293a1 1 0 011.414 0L10 8.586l4.293-4.293a1 1 0 111.414 1.414L11.414 10l4.293 4.293a1 1 0 01-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 01-1.414-1.414L8.586 10 4.293 5.707a1 1 0 010-1.414z"
                    clipRule="evenodd"
                  />
                </svg>
              </button>
            )}
          </div>

          {metadata.summary && (
            <div className="mb-4">
              <h4 className="text-white/80 text-sm font-medium mb-1">
                Summary
              </h4>
              <p className="text-white/90 text-sm leading-relaxed">
                {metadata.summary}
              </p>
            </div>
          )}

          {metadata.familyMembers.length > 0 && (
            <div>
              <h4 className="text-white/80 text-sm font-medium mb-2">
                Family Members
              </h4>
              <div className="flex flex-wrap gap-2">
                {metadata.familyMembers.map((member, i) => (
                  <span
                    key={i}
                    className="bg-white/20 px-2 py-1 rounded-lg text-white text-xs"
                  >
                    {member}
                  </span>
                ))}
              </div>
            </div>
          )}
        </div>
      </div>
    );
  }

  if (!content) {
    return (
      <div
        style={{ backgroundColor: themeColor }}
        className="rounded-2xl shadow-xl max-w-lg w-full mt-6"
      >
        <div className="bg-white/20 backdrop-blur-md p-6 rounded-2xl text-center">
          <p className="text-white/80">No content selected</p>
        </div>
      </div>
    );
  }

  return (
    <div
      style={{ backgroundColor: themeColor }}
      className="rounded-2xl shadow-xl max-w-lg w-full mt-6"
    >
      <div className="bg-white/20 backdrop-blur-md p-6 rounded-2xl">
        {/* Header */}
        <div className="flex items-start justify-between mb-4">
          <div>
            <h3 className="text-xl font-bold text-white">
              {content.title || content.fileName}
            </h3>
            <div className="flex items-center gap-2 mt-1">
              <span className="text-white/70 text-sm uppercase">
                {content.contentType}
              </span>
              <StatusBadge status={content.status} />
            </div>
          </div>
          {onClose && (
            <button
              onClick={onClose}
              className="text-white/60 hover:text-white transition-colors p-1"
            >
              <svg
                xmlns="http://www.w3.org/2000/svg"
                className="h-5 w-5"
                viewBox="0 0 20 20"
                fill="currentColor"
              >
                <path
                  fillRule="evenodd"
                  d="M4.293 4.293a1 1 0 011.414 0L10 8.586l4.293-4.293a1 1 0 111.414 1.414L11.414 10l4.293 4.293a1 1 0 01-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 01-1.414-1.414L8.586 10 4.293 5.707a1 1 0 010-1.414z"
                  clipRule="evenodd"
                />
              </svg>
            </button>
          )}
        </div>

        {/* Description */}
        {content.description && (
          <p className="text-white/90 text-sm mb-4">{content.description}</p>
        )}

        {/* Metadata Grid */}
        <div className="grid grid-cols-2 gap-3 mb-4">
          <div className="bg-white/10 rounded-lg p-3">
            <p className="text-white/60 text-xs mb-1">File Name</p>
            <p className="text-white text-sm truncate">{content.fileName}</p>
          </div>
          <div className="bg-white/10 rounded-lg p-3">
            <p className="text-white/60 text-xs mb-1">Size</p>
            <p className="text-white text-sm">
              {formatFileSize(content.fileSizeBytes)}
            </p>
          </div>
          <div className="bg-white/10 rounded-lg p-3">
            <p className="text-white/60 text-xs mb-1">Uploaded</p>
            <p className="text-white text-sm">
              {formatDate(content.uploadedAt)}
            </p>
          </div>
          <div className="bg-white/10 rounded-lg p-3">
            <p className="text-white/60 text-xs mb-1">Content Date</p>
            <p className="text-white text-sm">
              {formatDate(content.contentDate)}
            </p>
          </div>
        </div>

        {/* Family Members */}
        {content.familyMembers.length > 0 && (
          <div className="mb-4">
            <h4 className="text-white/80 text-sm font-medium mb-2">
              Family Members
            </h4>
            <div className="flex flex-wrap gap-2">
              {content.familyMembers.map((member, i) => (
                <span
                  key={i}
                  className="bg-white/20 px-2 py-1 rounded-lg text-white text-xs"
                >
                  {member}
                </span>
              ))}
            </div>
          </div>
        )}

        {/* Tags */}
        {content.tags.length > 0 && (
          <div className="mb-4">
            <h4 className="text-white/80 text-sm font-medium mb-2">Tags</h4>
            <div className="flex flex-wrap gap-2">
              {content.tags.map((tag, i) => (
                <span
                  key={i}
                  className="bg-white/10 px-2 py-1 rounded-lg text-white/80 text-xs"
                >
                  #{tag}
                </span>
              ))}
            </div>
          </div>
        )}

        {/* Processed Content Section */}
        {processedContent && (
          <>
            {/* Summary */}
            {processedContent.summary && (
              <div className="mb-4">
                <h4 className="text-white/80 text-sm font-medium mb-1">
                  AI Summary
                </h4>
                <p className="text-white/90 text-sm leading-relaxed bg-white/10 p-3 rounded-lg">
                  {processedContent.summary}
                </p>
              </div>
            )}

            {/* Entities */}
            {processedContent.entities.length > 0 && (
              <div className="mb-4">
                <h4 className="text-white/80 text-sm font-medium mb-2">
                  Extracted Entities
                </h4>
                <div className="flex flex-wrap gap-2">
                  {processedContent.entities.map((entity, i) => (
                    <EntityChip
                      key={i}
                      type={entity.type}
                      value={entity.value}
                      confidence={entity.confidence}
                    />
                  ))}
                </div>
              </div>
            )}

            {/* Keywords */}
            {processedContent.keywords.length > 0 && (
              <div className="mb-4">
                <h4 className="text-white/80 text-sm font-medium mb-2">
                  Keywords
                </h4>
                <div className="flex flex-wrap gap-2">
                  {processedContent.keywords.map((keyword, i) => (
                    <span
                      key={i}
                      className="bg-white/10 px-2 py-1 rounded-lg text-white/80 text-xs"
                    >
                      {keyword}
                    </span>
                  ))}
                </div>
              </div>
            )}

            {/* Sentiment */}
            {processedContent.sentiment && (
              <div className="mb-4">
                <h4 className="text-white/80 text-sm font-medium mb-1">
                  Sentiment
                </h4>
                <span className="bg-white/10 px-2 py-1 rounded-lg text-white/80 text-xs capitalize">
                  {processedContent.sentiment}
                </span>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}
