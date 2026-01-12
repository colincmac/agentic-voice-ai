"use client";

import { useState, useEffect, useCallback } from "react";
import { FamilyContent, ContentListResponse, ContentType } from "@/lib/types";
import { useAuthorizedFetch } from "@/hooks/useAuthorizedFetch";

export interface ContentListViewProps {
  themeColor: string;
  apiBaseUrl?: string;
  onSelectContent?: (contentId: string) => void;
  selectedContentId?: string;
  contentTypeFilter?: ContentType | null;
}

function ListIcon({ className }: { className?: string }) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 24 24"
      fill="currentColor"
      className={className}
    >
      <path
        fillRule="evenodd"
        d="M2.625 6.75a1.125 1.125 0 112.25 0 1.125 1.125 0 01-2.25 0zm4.875 0A.75.75 0 018.25 6h12a.75.75 0 010 1.5h-12a.75.75 0 01-.75-.75zM2.625 12a1.125 1.125 0 112.25 0 1.125 1.125 0 01-2.25 0zM7.5 12a.75.75 0 01.75-.75h12a.75.75 0 010 1.5h-12A.75.75 0 017.5 12zm-4.875 5.25a1.125 1.125 0 112.25 0 1.125 1.125 0 01-2.25 0zm4.875 0a.75.75 0 01.75-.75h12a.75.75 0 010 1.5h-12a.75.75 0 01-.75-.75z"
        clipRule="evenodd"
      />
    </svg>
  );
}

// Content type icons
function getContentTypeIcon(contentType: ContentType) {
  const iconClass = "w-4 h-4";
  switch (contentType) {
    case "Photo":
    case "DocumentImage":
      return (
        <svg
          xmlns="http://www.w3.org/2000/svg"
          viewBox="0 0 24 24"
          fill="currentColor"
          className={iconClass}
        >
          <path
            fillRule="evenodd"
            d="M1.5 6a2.25 2.25 0 012.25-2.25h16.5A2.25 2.25 0 0122.5 6v12a2.25 2.25 0 01-2.25 2.25H3.75A2.25 2.25 0 011.5 18V6zM3 16.06V18c0 .414.336.75.75.75h16.5A.75.75 0 0021 18v-1.94l-2.69-2.689a1.5 1.5 0 00-2.12 0l-.88.879.97.97a.75.75 0 11-1.06 1.06l-5.16-5.159a1.5 1.5 0 00-2.12 0L3 16.061zm10.125-7.81a1.125 1.125 0 112.25 0 1.125 1.125 0 01-2.25 0z"
            clipRule="evenodd"
          />
        </svg>
      );
    case "Video":
      return (
        <svg
          xmlns="http://www.w3.org/2000/svg"
          viewBox="0 0 24 24"
          fill="currentColor"
          className={iconClass}
        >
          <path d="M4.5 4.5a3 3 0 00-3 3v9a3 3 0 003 3h8.25a3 3 0 003-3v-9a3 3 0 00-3-3H4.5zM19.94 18.75l-2.69-2.69V7.94l2.69-2.69c.944-.945 2.56-.276 2.56 1.06v11.38c0 1.336-1.616 2.005-2.56 1.06z" />
        </svg>
      );
    case "Audio":
      return (
        <svg
          xmlns="http://www.w3.org/2000/svg"
          viewBox="0 0 24 24"
          fill="currentColor"
          className={iconClass}
        >
          <path d="M13.5 4.06c0-1.336-1.616-2.005-2.56-1.06l-4.5 4.5H4.508c-1.141 0-2.318.664-2.66 1.905A9.76 9.76 0 001.5 12c0 .898.121 1.768.35 2.595.341 1.24 1.518 1.905 2.659 1.905h1.93l4.5 4.5c.945.945 2.561.276 2.561-1.06V4.06z" />
        </svg>
      );
    default:
      return (
        <svg
          xmlns="http://www.w3.org/2000/svg"
          viewBox="0 0 24 24"
          fill="currentColor"
          className={iconClass}
        >
          <path
            fillRule="evenodd"
            d="M5.625 1.5c-1.036 0-1.875.84-1.875 1.875v17.25c0 1.035.84 1.875 1.875 1.875h12.75c1.035 0 1.875-.84 1.875-1.875V12.75A3.75 3.75 0 0016.5 9h-1.875a1.875 1.875 0 01-1.875-1.875V5.25A3.75 3.75 0 009 1.5H5.625z"
            clipRule="evenodd"
          />
        </svg>
      );
  }
}

// Status badge component
function StatusBadge({ status }: { status: string }) {
  const colors: Record<string, string> = {
    Pending: "bg-yellow-400/50",
    Processing: "bg-blue-400/50",
    Completed: "bg-green-400/50",
    Failed: "bg-red-400/50",
  };

  return (
    <span
      className={`w-2 h-2 rounded-full ${colors[status] || "bg-gray-400/50"}`}
    />
  );
}

// Individual content item
function ContentItem({
  content,
  isSelected,
  onClick,
}: {
  content: FamilyContent;
  isSelected: boolean;
  onClick: () => void;
}) {
  return (
    <div
      onClick={onClick}
      className={`p-3 rounded-xl cursor-pointer transition-all ${
        isSelected
          ? "bg-white/30 ring-2 ring-white"
          : "bg-white/10 hover:bg-white/20"
      }`}
    >
      <div className="flex items-start gap-3">
        <div className="bg-white/20 p-1.5 rounded-lg flex-shrink-0">
          {getContentTypeIcon(content.contentType)}
        </div>
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2">
            <h4 className="text-white text-sm font-medium truncate">
              {content.title || content.fileName}
            </h4>
            <StatusBadge status={content.status} />
          </div>
          <p className="text-white/60 text-xs truncate">{content.fileName}</p>
          {content.familyMembers.length > 0 && (
            <p className="text-white/50 text-xs mt-1 truncate">
              {content.familyMembers.join(", ")}
            </p>
          )}
        </div>
      </div>
    </div>
  );
}

const CONTENT_TYPE_OPTIONS: { value: ContentType | ""; label: string }[] = [
  { value: "", label: "All Types" },
  { value: "Document", label: "Documents" },
  { value: "DocumentImage", label: "Scanned Documents" },
  { value: "Photo", label: "Photos" },
  { value: "Video", label: "Videos" },
  { value: "Audio", label: "Audio" },
];

export function ContentListView({
  themeColor,
  apiBaseUrl = "",
  onSelectContent,
  selectedContentId,
  contentTypeFilter,
}: ContentListViewProps) {
  const [content, setContent] = useState<FamilyContent[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [continuationToken, setContinuationToken] = useState<
    string | undefined
  >();
  const [hasMore, setHasMore] = useState(false);
  const [filter, setFilter] = useState<ContentType | "">(
    contentTypeFilter || ""
  );
  const authorizedFetch = useAuthorizedFetch();

  const fetchContent = useCallback(
    async (append = false, currentContinuationToken?: string) => {
      setIsLoading(true);
      setError(null);

      try {
        const params = new URLSearchParams();
        params.set("pageSize", "20");
        if (filter) {
          params.set("contentType", filter);
        }
        if (append && currentContinuationToken) {
          params.set("continuationToken", currentContinuationToken);
        }

        const response = await authorizedFetch(
          `${apiBaseUrl}/api/content?${params.toString()}`
        );

        if (!response.ok) {
          throw new Error(`Failed to fetch content: ${response.status}`);
        }

        const data: ContentListResponse = await response.json();

        if (append) {
          setContent((prev) => [...prev, ...data.items]);
        } else {
          setContent(data.items);
        }

        setContinuationToken(data.continuationToken || undefined);
        setHasMore(!!data.continuationToken);
      } catch (err) {
        setError(err instanceof Error ? err.message : "Failed to load content");
      } finally {
        setIsLoading(false);
      }
    },
    [apiBaseUrl, filter, authorizedFetch]
  );

  // Initial load and filter change
  useEffect(() => {
    fetchContent(false);
  }, [fetchContent]);

  // Update filter from props
  useEffect(() => {
    if (contentTypeFilter !== undefined) {
      setFilter(contentTypeFilter || "");
    }
  }, [contentTypeFilter]);

  return (
    <div
      style={{ backgroundColor: themeColor }}
      className="rounded-2xl shadow-xl max-w-md w-full mt-6"
    >
      <div className="bg-white/20 backdrop-blur-md p-6 rounded-2xl">
        <div className="flex items-center gap-3 mb-4">
          <div className="bg-white/30 p-2 rounded-full">
            <ListIcon className="w-6 h-6 text-white" />
          </div>
          <div>
            <h3 className="text-xl font-bold text-white">Family Content</h3>
            <p className="text-white/80 text-sm">
              {content.length} item{content.length !== 1 ? "s" : ""}
            </p>
          </div>
        </div>

        {/* Filter */}
        <div className="mb-4">
          <select
            value={filter}
            onChange={(e) => setFilter(e.target.value as ContentType | "")}
            className="w-full bg-white/20 text-white rounded-xl px-4 py-2 border border-white/30 focus:outline-none focus:ring-2 focus:ring-white/50 text-sm"
          >
            {CONTENT_TYPE_OPTIONS.map((opt) => (
              <option key={opt.value} value={opt.value} className="text-black">
                {opt.label}
              </option>
            ))}
          </select>
        </div>

        {/* Error */}
        {error && (
          <div className="p-3 bg-red-500/20 rounded-xl mb-4">
            <p className="text-red-200 text-sm">{error}</p>
            <button
              onClick={() => fetchContent(false)}
              className="text-red-200 text-sm underline mt-1"
            >
              Try again
            </button>
          </div>
        )}

        {/* Content List */}
        {content.length === 0 && !isLoading ? (
          <div className="text-center py-8">
            <ListIcon className="w-12 h-12 mx-auto text-white/30 mb-3" />
            <p className="text-white/60">No content found</p>
            <p className="text-white/40 text-sm mt-1">
              Upload some family documents, photos, or recordings to get started
            </p>
          </div>
        ) : (
          <div className="space-y-2 max-h-[400px] overflow-y-auto">
            {content.map((item) => (
              <ContentItem
                key={item.id}
                content={item}
                isSelected={selectedContentId === item.id}
                onClick={() => onSelectContent?.(item.id)}
              />
            ))}

            {/* Loading indicator */}
            {isLoading && (
              <div className="text-center py-4">
                <div className="animate-spin rounded-full h-6 w-6 border-b-2 border-white mx-auto" />
              </div>
            )}

            {/* Load more button */}
            {hasMore && !isLoading && (
              <button
                onClick={() => fetchContent(true, continuationToken)}
                className="w-full py-2 text-white/60 hover:text-white text-sm transition-colors"
              >
                Load more...
              </button>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
