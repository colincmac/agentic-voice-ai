"use client";

import { useState, useRef } from "react";
import { ContentType } from "@/lib/types";
import { useAuthorizedFetch } from "@/hooks/useAuthorizedFetch";

export interface ContentUploadFormProps {
  themeColor: string;
  apiBaseUrl?: string;
  onUploadSuccess?: (contentId: string) => void;
  onUploadError?: (error: string) => void;
}

function UploadIcon({ className }: { className?: string }) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 24 24"
      fill="currentColor"
      className={className}
    >
      <path
        fillRule="evenodd"
        d="M11.47 2.47a.75.75 0 011.06 0l4.5 4.5a.75.75 0 01-1.06 1.06l-3.22-3.22V16.5a.75.75 0 01-1.5 0V4.81L8.03 8.03a.75.75 0 01-1.06-1.06l4.5-4.5zM3 15.75a.75.75 0 01.75.75v2.25a1.5 1.5 0 001.5 1.5h13.5a1.5 1.5 0 001.5-1.5V16.5a.75.75 0 011.5 0v2.25a3 3 0 01-3 3H5.25a3 3 0 01-3-3V16.5a.75.75 0 01.75-.75z"
        clipRule="evenodd"
      />
    </svg>
  );
}

const CONTENT_TYPES: { value: ContentType; label: string }[] = [
  { value: "Document", label: "Document" },
  { value: "DocumentImage", label: "Scanned Document" },
  { value: "Photo", label: "Photo" },
  { value: "Video", label: "Video" },
  { value: "Audio", label: "Audio Recording" },
];

export function ContentUploadForm({
  themeColor,
  apiBaseUrl = "",
  onUploadSuccess,
  onUploadError,
}: ContentUploadFormProps) {
  const [file, setFile] = useState<File | null>(null);
  const [contentType, setContentType] = useState<ContentType>("Document");
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [familyMembers, setFamilyMembers] = useState("");
  const [tags, setTags] = useState("");
  const [isUploading, setIsUploading] = useState(false);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const [uploadSuccess, setUploadSuccess] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const authorizedFetch = useAuthorizedFetch();
  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const selectedFile = e.target.files?.[0];
    if (selectedFile) {
      setFile(selectedFile);
      setUploadError(null);
      setUploadSuccess(false);
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!file) {
      setUploadError("Please select a file to upload");
      return;
    }

    setIsUploading(true);
    setUploadError(null);
    setUploadSuccess(false);

    try {
      const formData = new FormData();
      formData.append("file", file);
      formData.append("contentType", contentType);
      if (title) formData.append("title", title);
      if (description) formData.append("description", description);
      if (familyMembers) formData.append("familyMembers", familyMembers);
      if (tags) formData.append("tags", tags);

      const response = await authorizedFetch(`${apiBaseUrl}/api/content/upload`, {
        method: "POST",
        body: formData,
      });

      if (!response.ok) {
        const errorText = await response.text();
        throw new Error(errorText || `Upload failed: ${response.status}`);
      }

      const result = await response.json();
      setUploadSuccess(true);
      onUploadSuccess?.(result.contentId);

      // Reset form
      setFile(null);
      setTitle("");
      setDescription("");
      setFamilyMembers("");
      setTags("");
      if (fileInputRef.current) {
        fileInputRef.current.value = "";
      }
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Upload failed";
      setUploadError(errorMessage);
      onUploadError?.(errorMessage);
    } finally {
      setIsUploading(false);
    }
  };

  return (
    <div
      style={{ backgroundColor: themeColor }}
      className="rounded-2xl shadow-xl max-w-md w-full mt-6"
    >
      <div className="bg-white/20 backdrop-blur-md p-6 rounded-2xl">
        <div className="flex items-center gap-3 mb-4">
          <div className="bg-white/30 p-2 rounded-full">
            <UploadIcon className="w-6 h-6 text-white" />
          </div>
          <div>
            <h3 className="text-xl font-bold text-white">
              Upload Family Content
            </h3>
            <p className="text-white/80 text-sm">
              Add documents, photos, videos, or audio
            </p>
          </div>
        </div>

        <form onSubmit={handleSubmit} className="space-y-4">
          {/* File Input */}
          <div>
            <label className="block text-white/80 text-sm font-medium mb-2">
              File *
            </label>
            <div
              onClick={() => fileInputRef.current?.click()}
              className="border-2 border-dashed border-white/30 rounded-xl p-4 text-center cursor-pointer hover:border-white/50 transition-colors"
            >
              <input
                ref={fileInputRef}
                type="file"
                onChange={handleFileChange}
                className="hidden"
                accept=".pdf,.doc,.docx,.txt,.rtf,.jpg,.jpeg,.png,.gif,.bmp,.tiff,.mp4,.avi,.mov,.wmv,.mkv,.mp3,.wav,.m4a,.aac,.flac"
              />
              {file ? (
                <div className="text-white">
                  <p className="font-medium">{file.name}</p>
                  <p className="text-sm text-white/60">
                    {(file.size / 1024 / 1024).toFixed(2)} MB
                  </p>
                </div>
              ) : (
                <div className="text-white/60">
                  <UploadIcon className="w-8 h-8 mx-auto mb-2" />
                  <p>Click to select a file</p>
                </div>
              )}
            </div>
          </div>

          {/* Content Type */}
          <div>
            <label className="block text-white/80 text-sm font-medium mb-2">
              Content Type *
            </label>
            <select
              value={contentType}
              onChange={(e) => setContentType(e.target.value as ContentType)}
              className="w-full bg-white/20 text-white rounded-xl px-4 py-2 border border-white/30 focus:outline-none focus:ring-2 focus:ring-white/50"
            >
              {CONTENT_TYPES.map((ct) => (
                <option key={ct.value} value={ct.value} className="text-black">
                  {ct.label}
                </option>
              ))}
            </select>
          </div>

          {/* Title */}
          <div>
            <label className="block text-white/80 text-sm font-medium mb-2">
              Title
            </label>
            <input
              type="text"
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              placeholder="e.g., Family Reunion 1985"
              className="w-full bg-white/20 text-white rounded-xl px-4 py-2 border border-white/30 focus:outline-none focus:ring-2 focus:ring-white/50 placeholder:text-white/40"
            />
          </div>

          {/* Description */}
          <div>
            <label className="block text-white/80 text-sm font-medium mb-2">
              Description
            </label>
            <textarea
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="Describe what this content is about..."
              rows={2}
              className="w-full bg-white/20 text-white rounded-xl px-4 py-2 border border-white/30 focus:outline-none focus:ring-2 focus:ring-white/50 placeholder:text-white/40 resize-none"
            />
          </div>

          {/* Family Members */}
          <div>
            <label className="block text-white/80 text-sm font-medium mb-2">
              Family Members
            </label>
            <input
              type="text"
              value={familyMembers}
              onChange={(e) => setFamilyMembers(e.target.value)}
              placeholder="John Doe, Jane Doe (comma-separated)"
              className="w-full bg-white/20 text-white rounded-xl px-4 py-2 border border-white/30 focus:outline-none focus:ring-2 focus:ring-white/50 placeholder:text-white/40"
            />
          </div>

          {/* Tags */}
          <div>
            <label className="block text-white/80 text-sm font-medium mb-2">
              Tags
            </label>
            <input
              type="text"
              value={tags}
              onChange={(e) => setTags(e.target.value)}
              placeholder="wedding, reunion, 1980s (comma-separated)"
              className="w-full bg-white/20 text-white rounded-xl px-4 py-2 border border-white/30 focus:outline-none focus:ring-2 focus:ring-white/50 placeholder:text-white/40"
            />
          </div>

          {/* Error Message */}
          {uploadError && (
            <div className="p-3 bg-red-500/20 rounded-xl">
              <p className="text-red-200 text-sm">{uploadError}</p>
            </div>
          )}

          {/* Success Message */}
          {uploadSuccess && (
            <div className="p-3 bg-green-500/20 rounded-xl">
              <p className="text-green-200 text-sm">
                Content uploaded successfully! It will be processed shortly.
              </p>
            </div>
          )}

          {/* Submit Button */}
          <button
            type="submit"
            disabled={isUploading || !file}
            className="w-full py-3 rounded-xl bg-white/30 hover:bg-white/40 disabled:opacity-50 disabled:cursor-not-allowed text-white font-bold transition-all flex items-center justify-center gap-2"
          >
            {isUploading ? (
              <>
                <div className="animate-spin rounded-full h-5 w-5 border-b-2 border-white" />
                Uploading...
              </>
            ) : (
              <>
                <UploadIcon className="w-5 h-5" />
                Upload Content
              </>
            )}
          </button>
        </form>
      </div>
    </div>
  );
}
