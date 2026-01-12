"use client";

import { SourceReference } from "@/lib/types";

export interface SourceReferenceCardProps {
  reference: SourceReference;
  themeColor: string;
  onSelect?: (contentId: string) => void;
  isSelected?: boolean;
}

// Icon components for different content types
function DocumentIcon({ className }: { className?: string }) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 24 24"
      fill="currentColor"
      className={className}
    >
      <path
        fillRule="evenodd"
        d="M5.625 1.5c-1.036 0-1.875.84-1.875 1.875v17.25c0 1.035.84 1.875 1.875 1.875h12.75c1.035 0 1.875-.84 1.875-1.875V12.75A3.75 3.75 0 0016.5 9h-1.875a1.875 1.875 0 01-1.875-1.875V5.25A3.75 3.75 0 009 1.5H5.625zM7.5 15a.75.75 0 01.75-.75h7.5a.75.75 0 010 1.5h-7.5A.75.75 0 017.5 15zm.75 2.25a.75.75 0 000 1.5H12a.75.75 0 000-1.5H8.25z"
        clipRule="evenodd"
      />
      <path d="M12.971 1.816A5.23 5.23 0 0114.25 5.25v1.875c0 .207.168.375.375.375H16.5a5.23 5.23 0 013.434 1.279 9.768 9.768 0 00-6.963-6.963z" />
    </svg>
  );
}

function PhotoIcon({ className }: { className?: string }) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 24 24"
      fill="currentColor"
      className={className}
    >
      <path
        fillRule="evenodd"
        d="M1.5 6a2.25 2.25 0 012.25-2.25h16.5A2.25 2.25 0 0122.5 6v12a2.25 2.25 0 01-2.25 2.25H3.75A2.25 2.25 0 011.5 18V6zM3 16.06V18c0 .414.336.75.75.75h16.5A.75.75 0 0021 18v-1.94l-2.69-2.689a1.5 1.5 0 00-2.12 0l-.88.879.97.97a.75.75 0 11-1.06 1.06l-5.16-5.159a1.5 1.5 0 00-2.12 0L3 16.061zm10.125-7.81a1.125 1.125 0 112.25 0 1.125 1.125 0 01-2.25 0z"
        clipRule="evenodd"
      />
    </svg>
  );
}

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

function AudioIcon({ className }: { className?: string }) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 24 24"
      fill="currentColor"
      className={className}
    >
      <path d="M13.5 4.06c0-1.336-1.616-2.005-2.56-1.06l-4.5 4.5H4.508c-1.141 0-2.318.664-2.66 1.905A9.76 9.76 0 001.5 12c0 .898.121 1.768.35 2.595.341 1.24 1.518 1.905 2.659 1.905h1.93l4.5 4.5c.945.945 2.561.276 2.561-1.06V4.06zM18.584 5.106a.75.75 0 011.06 0c3.808 3.807 3.808 9.98 0 13.788a.75.75 0 11-1.06-1.06 8.25 8.25 0 000-11.668.75.75 0 010-1.06z" />
      <path d="M15.932 7.757a.75.75 0 011.061 0 6 6 0 010 8.486.75.75 0 01-1.06-1.061 4.5 4.5 0 000-6.364.75.75 0 010-1.06z" />
    </svg>
  );
}

function getContentIcon(contentType: string) {
  switch (contentType) {
    case "Photo":
    case "DocumentImage":
      return PhotoIcon;
    case "Video":
      return VideoIcon;
    case "Audio":
      return AudioIcon;
    default:
      return DocumentIcon;
  }
}

export function SourceReferenceCard({
  reference,
  themeColor,
  onSelect,
  isSelected,
}: SourceReferenceCardProps) {
  const IconComponent = getContentIcon(reference.contentType);
  const relevancePercent = Math.round(reference.relevanceScore * 100);

  return (
    <div
      onClick={() => onSelect?.(reference.contentId)}
      className={`bg-white/20 backdrop-blur-md p-4 rounded-xl cursor-pointer transition-all hover:scale-[1.02] hover:bg-white/30 ${
        isSelected ? "ring-2 ring-white" : ""
      }`}
      style={{ backgroundColor: `${themeColor}40` }}
    >
      <div className="flex items-start gap-3">
        <div className="bg-white/30 p-2 rounded-lg flex-shrink-0">
          <IconComponent className="w-5 h-5 text-white" />
        </div>
        <div className="flex-1 min-w-0">
          <h4 className="text-white font-medium truncate">
            {reference.title || "Untitled"}
          </h4>
          <div className="flex items-center gap-2 mt-1">
            <span className="text-white/70 text-xs uppercase">
              {reference.contentType}
            </span>
            <span className="text-white/50 text-xs">•</span>
            <span className="text-white/70 text-xs">
              {relevancePercent}% match
            </span>
          </div>
        </div>
      </div>
    </div>
  );
}

export interface SourceReferenceListProps {
  references: SourceReference[];
  themeColor: string;
  query?: string;
  onSelect?: (contentId: string) => void;
  selectedContentId?: string;
}

export function SourceReferenceList({
  references,
  themeColor,
  query,
  onSelect,
  selectedContentId,
}: SourceReferenceListProps) {
  if (references.length === 0) {
    return (
      <div
        style={{ backgroundColor: themeColor }}
        className="rounded-2xl shadow-xl max-w-md w-full mt-6"
      >
        <div className="bg-white/20 backdrop-blur-md p-6 rounded-2xl text-center">
          <DocumentIcon className="w-12 h-12 mx-auto text-white/50 mb-3" />
          <p className="text-white/80">
            {query
              ? `No content found for "${query}"`
              : "No content to display"}
          </p>
        </div>
      </div>
    );
  }

  return (
    <div
      style={{ backgroundColor: themeColor }}
      className="rounded-2xl shadow-xl max-w-md w-full mt-6"
    >
      <div className="bg-white/20 backdrop-blur-md p-6 rounded-2xl">
        <h3 className="text-xl font-bold text-white mb-4">
          {query ? `Results for "${query}"` : "Search Results"}
        </h3>
        <p className="text-white/70 text-sm mb-4">
          Found {references.length} item{references.length !== 1 ? "s" : ""}
        </p>
        <div className="space-y-3">
          {references.map((ref) => (
            <SourceReferenceCard
              key={ref.contentId}
              reference={ref}
              themeColor={themeColor}
              onSelect={onSelect}
              isSelected={selectedContentId === ref.contentId}
            />
          ))}
        </div>
      </div>
    </div>
  );
}
