"use client";

import { useState, useEffect, useCallback } from "react";
import {
  GraphNeighborhood,
  GraphNode,
  GraphEdge,
  GraphEntityType,
  FullGraphResponse,
  CommunitySummaryState,
} from "@/lib/types";

// FamilyAI backend URL - used for REST API calls
const FAMILY_AI_API_URL =
  process.env.NEXT_PUBLIC_FAMILY_AI_API_URL || "http://localhost:8000";

interface FamilyGraphViewProps {
  neighborhood?: GraphNeighborhood;
  onNodeClick?: (nodeId: string) => void;
  onAddRelationship?: (
    fromId: string,
    toId: string,
    relationshipType: string
  ) => void;
  onCreateEntity?: (
    name: string,
    entityType: string,
    attributes?: string
  ) => void;
  onUpdateEntity?: (
    entityId: string,
    newName?: string,
    attributes?: string
  ) => void;
  onDeleteEntity?: (entityId: string) => void;
  apiBaseUrl?: string;
  autoPreload?: boolean;
}

const ENTITY_COLORS: Record<GraphEntityType, string> = {
  Person: "#4f46e5", // Indigo
  Place: "#059669", // Emerald
  Event: "#d97706", // Amber
  Source: "#6b7280", // Gray
};

const ENTITY_TYPES = ["Person", "Place", "Event"];

const RELATIONSHIP_TYPES = [
  "ParentOf",
  "ChildOf",
  "SpouseOf",
  "SiblingOf",
  "LivedIn",
  "AttendedEvent",
  "BornAt",
  "DiedAt",
  "WorkedAt",
  "KnewPerson",
  "RelatedTo",
];

type FormMode = "none" | "addEntity" | "editEntity" | "addRelationship" | "confirmDelete";

export function FamilyGraphView({
  neighborhood,
  onNodeClick,
  onAddRelationship,
  onCreateEntity,
  onUpdateEntity,
  onDeleteEntity,
  apiBaseUrl = FAMILY_AI_API_URL,
  autoPreload = true,
}: FamilyGraphViewProps) {
  const [selectedNode, setSelectedNode] = useState<string | null>(null);
  const [formMode, setFormMode] = useState<FormMode>("none");
  const [deleteTarget, setDeleteTarget] = useState<{ id: string; label: string } | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [preloadedGraph, setPreloadedGraph] = useState<FullGraphResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [communitySummaries, setCommunitySummaries] = useState<CommunitySummaryState[]>([]);
  const [showCommunities, setShowCommunities] = useState(false);
  
  // Entity form state
  const [entityForm, setEntityForm] = useState({
    name: "",
    entityType: "Person",
    attributes: "",
  });
  
  // Edit entity form state
  const [editForm, setEditForm] = useState({
    entityId: "",
    newName: "",
    attributes: "",
  });
  
  // Relationship form state
  const [relationshipForm, setRelationshipForm] = useState({
    fromId: "",
    toId: "",
    relationshipType: "RelatedTo",
  });

  // Preload the user's graph when component mounts
  const loadGraph = useCallback(async () => {
    setIsLoading(true);
    setError(null);
    try {
      const response = await fetch(`${apiBaseUrl}/api/graph`, {
        method: "GET",
        headers: {
          "Content-Type": "application/json",
        },
        credentials: "include",
      });

      if (response.ok) {
        const data: FullGraphResponse = await response.json();
        setPreloadedGraph(data);
        setCommunitySummaries(data.communitySummaries || []);
      } else if (response.status === 401) {
        // User not authenticated - this is expected in some scenarios
        setError("Please sign in to view your knowledge graph.");
      } else {
        setError("Failed to load graph data.");
      }
    } catch (error) {
      // Network error or CORS issue - likely the backend isn't running
      console.log("Graph API not available - using agent-provided data", error);
    } finally {
      setIsLoading(false);
    }
  }, [apiBaseUrl]);

  useEffect(() => {
    if (autoPreload) {
      loadGraph();
    }
  }, [autoPreload, loadGraph]);

  const handleNodeClick = (nodeId: string) => {
    setSelectedNode(nodeId);
    onNodeClick?.(nodeId);
  };

  const handleCreateEntity = () => {
    if (entityForm.name && entityForm.entityType) {
      onCreateEntity?.(
        entityForm.name,
        entityForm.entityType,
        entityForm.attributes || undefined
      );
      setEntityForm({ name: "", entityType: "Person", attributes: "" });
      setFormMode("none");
    }
  };

  const handleUpdateEntity = () => {
    if (editForm.entityId) {
      onUpdateEntity?.(
        editForm.entityId,
        editForm.newName || undefined,
        editForm.attributes || undefined
      );
      setEditForm({ entityId: "", newName: "", attributes: "" });
      setFormMode("none");
    }
  };

  const initiateDeleteEntity = (entityId: string, entityLabel: string) => {
    setDeleteTarget({ id: entityId, label: entityLabel });
    setFormMode("confirmDelete");
  };

  const confirmDeleteEntity = () => {
    if (deleteTarget) {
      onDeleteEntity?.(deleteTarget.id);
      setDeleteTarget(null);
      setFormMode("none");
    }
  };

  const cancelDelete = () => {
    setDeleteTarget(null);
    setFormMode("none");
  };

  const handleAddRelationship = () => {
    if (
      relationshipForm.fromId &&
      relationshipForm.toId &&
      relationshipForm.relationshipType
    ) {
      onAddRelationship?.(
        relationshipForm.fromId,
        relationshipForm.toId,
        relationshipForm.relationshipType
      );
      setRelationshipForm({ fromId: "", toId: "", relationshipType: "RelatedTo" });
      setFormMode("none");
    }
  };

  const startEditEntity = (node: GraphNode) => {
    setEditForm({
      entityId: node.id,
      newName: node.label,
      attributes: "",
    });
    setFormMode("editEntity");
  };

  // Use preloaded graph if available, otherwise use the neighborhood from props
  const displayNodes = preloadedGraph?.nodes || neighborhood?.nodes || [];
  const displayEdges = preloadedGraph?.edges || neighborhood?.edges || [];
  const centerNode = neighborhood?.centerNode || displayNodes[0];

  // Create a map for quick node lookup
  const nodeMap = new Map(displayNodes.map((n) => [n.id, n]));

  return (
    <div className="bg-white/10 backdrop-blur-sm rounded-2xl p-6 shadow-lg">
      <div className="flex justify-between items-center mb-4">
        <h3 className="text-xl font-semibold text-white">Knowledge Graph</h3>
        <div className="flex gap-2">
          <button
            onClick={() => loadGraph()}
            disabled={isLoading}
            className="px-3 py-1 rounded-lg text-white text-sm bg-white/20 hover:bg-white/30 transition-all disabled:opacity-50"
            title="Refresh graph"
          >
            {isLoading ? "Loading..." : "🔄 Refresh"}
          </button>
          <button
            onClick={() => setFormMode(formMode === "addEntity" ? "none" : "addEntity")}
            className={`px-3 py-1 rounded-lg text-white text-sm transition-all ${
              formMode === "addEntity" ? "bg-white/30" : "bg-white/20 hover:bg-white/30"
            }`}
          >
            {formMode === "addEntity" ? "Cancel" : "+ Add Entity"}
          </button>
          {displayNodes.length > 0 && (
            <button
              onClick={() => setFormMode(formMode === "addRelationship" ? "none" : "addRelationship")}
              className={`px-3 py-1 rounded-lg text-white text-sm transition-all ${
                formMode === "addRelationship" ? "bg-white/30" : "bg-white/20 hover:bg-white/30"
              }`}
            >
              {formMode === "addRelationship" ? "Cancel" : "+ Add Relationship"}
            </button>
          )}
        </div>
      </div>

      {/* Legend */}
      <div className="flex gap-4 mb-4 flex-wrap">
        {(Object.entries(ENTITY_COLORS) as [GraphEntityType, string][]).map(
          ([type, color]) => (
            <div key={type} className="flex items-center gap-2">
              <div
                className="w-3 h-3 rounded-full"
                style={{ backgroundColor: color }}
              />
              <span className="text-white/70 text-xs">{type}</span>
            </div>
          )
        )}
      </div>

      {/* Error State */}
      {error && (
        <div className="bg-red-500/20 border border-red-500/40 rounded-xl p-4 mb-4">
          <p className="text-white/80 text-sm">{error}</p>
        </div>
      )}

      {/* Loading State */}
      {isLoading && (
        <div className="text-center py-4">
          <p className="text-white/70">Loading graph data...</p>
        </div>
      )}

      {/* Community Summaries Toggle */}
      {communitySummaries.length > 0 && (
        <div className="mb-4">
          <button
            onClick={() => setShowCommunities(!showCommunities)}
            className="px-3 py-1 rounded-lg text-white text-sm bg-white/20 hover:bg-white/30 transition-all"
          >
            {showCommunities ? "Hide" : "Show"} Community Summaries ({communitySummaries.length})
          </button>
          {showCommunities && (
            <div className="mt-3 space-y-2 max-h-48 overflow-y-auto">
              {communitySummaries.map((summary) => (
                <div key={summary.communityId} className="bg-white/10 rounded-lg p-3">
                  <h5 className="text-white font-medium text-sm">{summary.name || "Community"}</h5>
                  {summary.summaryText && (
                    <p className="text-white/70 text-xs mt-1">{summary.summaryText}</p>
                  )}
                  {summary.themes.length > 0 && (
                    <div className="flex flex-wrap gap-1 mt-2">
                      {summary.themes.map((theme, idx) => (
                        <span key={idx} className="px-2 py-0.5 bg-white/20 rounded text-white/80 text-xs">
                          {theme}
                        </span>
                      ))}
                    </div>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {/* Add Entity Form */}
      {formMode === "addEntity" && (
        <div className="bg-white/10 rounded-xl p-4 mb-4">
          <h4 className="text-white font-medium mb-3">Add New Entity</h4>
          <div className="grid grid-cols-1 gap-3">
            <input
              type="text"
              placeholder="Entity name (e.g., 'John Smith', 'New York')"
              value={entityForm.name}
              onChange={(e) =>
                setEntityForm({ ...entityForm, name: e.target.value })
              }
              className="bg-white/20 text-white placeholder-white/50 rounded-lg px-3 py-2 text-sm"
            />
            <select
              value={entityForm.entityType}
              onChange={(e) =>
                setEntityForm({ ...entityForm, entityType: e.target.value })
              }
              className="bg-white/20 text-white rounded-lg px-3 py-2 text-sm"
            >
              {ENTITY_TYPES.map((type) => (
                <option key={type} value={type}>
                  {type}
                </option>
              ))}
            </select>
            <input
              type="text"
              placeholder="Attributes (optional, e.g., 'birthYear:1950;occupation:farmer')"
              value={entityForm.attributes}
              onChange={(e) =>
                setEntityForm({ ...entityForm, attributes: e.target.value })
              }
              className="bg-white/20 text-white placeholder-white/50 rounded-lg px-3 py-2 text-sm"
            />
            <button
              onClick={handleCreateEntity}
              disabled={!entityForm.name}
              className="bg-white/30 hover:bg-white/40 disabled:bg-white/10 disabled:cursor-not-allowed text-white rounded-lg px-4 py-2 text-sm transition-all"
            >
              Create Entity
            </button>
          </div>
        </div>
      )}

      {/* Edit Entity Form */}
      {formMode === "editEntity" && (
        <div className="bg-white/10 rounded-xl p-4 mb-4">
          <h4 className="text-white font-medium mb-3">Edit Entity</h4>
          <div className="grid grid-cols-1 gap-3">
            <input
              type="text"
              placeholder="New name (leave empty to keep current)"
              value={editForm.newName}
              onChange={(e) =>
                setEditForm({ ...editForm, newName: e.target.value })
              }
              className="bg-white/20 text-white placeholder-white/50 rounded-lg px-3 py-2 text-sm"
            />
            <input
              type="text"
              placeholder="Add/update attributes (e.g., 'birthYear:1950;occupation:farmer')"
              value={editForm.attributes}
              onChange={(e) =>
                setEditForm({ ...editForm, attributes: e.target.value })
              }
              className="bg-white/20 text-white placeholder-white/50 rounded-lg px-3 py-2 text-sm"
            />
            <div className="flex gap-2">
              <button
                onClick={handleUpdateEntity}
                className="flex-1 bg-white/30 hover:bg-white/40 text-white rounded-lg px-4 py-2 text-sm transition-all"
              >
                Update Entity
              </button>
              <button
                onClick={() => setFormMode("none")}
                className="bg-white/20 hover:bg-white/30 text-white rounded-lg px-4 py-2 text-sm transition-all"
              >
                Cancel
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Add Relationship Form */}
      {formMode === "addRelationship" && (
        <div className="bg-white/10 rounded-xl p-4 mb-4">
          <h4 className="text-white font-medium mb-3">Add New Relationship</h4>
          <div className="grid grid-cols-1 gap-3">
            <select
              value={relationshipForm.fromId}
              onChange={(e) =>
                setRelationshipForm({ ...relationshipForm, fromId: e.target.value })
              }
              className="bg-white/20 text-white rounded-lg px-3 py-2 text-sm"
            >
              <option value="">Select source entity...</option>
              {displayNodes.map((node) => (
                <option key={node.id} value={node.id}>
                  {node.label} ({node.type})
                </option>
              ))}
            </select>
            <select
              value={relationshipForm.relationshipType}
              onChange={(e) =>
                setRelationshipForm({
                  ...relationshipForm,
                  relationshipType: e.target.value,
                })
              }
              className="bg-white/20 text-white rounded-lg px-3 py-2 text-sm"
            >
              {RELATIONSHIP_TYPES.map((type) => (
                <option key={type} value={type}>
                  {type}
                </option>
              ))}
            </select>
            <select
              value={relationshipForm.toId}
              onChange={(e) =>
                setRelationshipForm({ ...relationshipForm, toId: e.target.value })
              }
              className="bg-white/20 text-white rounded-lg px-3 py-2 text-sm"
            >
              <option value="">Select target entity...</option>
              {displayNodes.map((node) => (
                <option key={node.id} value={node.id}>
                  {node.label} ({node.type})
                </option>
              ))}
            </select>
            <button
              onClick={handleAddRelationship}
              disabled={!relationshipForm.fromId || !relationshipForm.toId}
              className="bg-white/30 hover:bg-white/40 disabled:bg-white/10 disabled:cursor-not-allowed text-white rounded-lg px-4 py-2 text-sm transition-all"
            >
              Create Relationship
            </button>
          </div>
        </div>
      )}

      {/* Delete Confirmation Dialog */}
      {formMode === "confirmDelete" && deleteTarget && (
        <div className="bg-red-500/20 border border-red-500/40 rounded-xl p-4 mb-4">
          <h4 className="text-white font-medium mb-2">Confirm Delete</h4>
          <p className="text-white/80 text-sm mb-4">
            Are you sure you want to delete <strong>{deleteTarget.label}</strong> and all its relationships? This action cannot be undone.
          </p>
          <div className="flex gap-2">
            <button
              onClick={confirmDeleteEntity}
              className="flex-1 bg-red-500 hover:bg-red-600 text-white rounded-lg px-4 py-2 text-sm transition-all"
            >
              Delete
            </button>
            <button
              onClick={cancelDelete}
              className="flex-1 bg-white/20 hover:bg-white/30 text-white rounded-lg px-4 py-2 text-sm transition-all"
            >
              Cancel
            </button>
          </div>
        </div>
      )}

      {/* Empty State */}
      {displayNodes.length === 0 && formMode === "none" && !isLoading && !error && (
        <p className="text-white/70 text-center py-8">
          No graph data available. Upload some family content to start building your knowledge graph, or add entities manually using the button above.
        </p>
      )}

      {/* Graph Statistics */}
      {displayNodes.length > 0 && (
        <div className="bg-white/10 rounded-lg p-3 mb-4">
          <div className="flex gap-4 text-white/80 text-sm">
            <span>📊 {displayNodes.length} entities</span>
            <span>🔗 {displayEdges.length} relationships</span>
            {communitySummaries.length > 0 && (
              <span>🏘️ {communitySummaries.length} communities</span>
            )}
          </div>
        </div>
      )}

      {/* Center Node */}
      {centerNode && (
        <div className="mb-4">
          <h4 className="text-white/70 text-sm mb-2">Center Entity</h4>
          <NodeCard
            node={centerNode}
            isCenter={true}
            isSelected={selectedNode === centerNode.id}
            onClick={() => handleNodeClick(centerNode.id)}
            onEdit={() => startEditEntity(centerNode)}
            onDelete={() => initiateDeleteEntity(centerNode.id, centerNode.label)}
          />
        </div>
      )}

      {/* Relationships */}
      {displayEdges.length > 0 && (
        <div className="mb-4">
          <h4 className="text-white/70 text-sm mb-2">
            Relationships ({displayEdges.length})
          </h4>
          <div className="space-y-2 max-h-48 overflow-y-auto">
            {displayEdges.map((edge) => (
              <EdgeCard
                key={edge.id}
                edge={edge}
                fromNode={nodeMap.get(edge.from)}
                toNode={nodeMap.get(edge.to)}
              />
            ))}
          </div>
        </div>
      )}

      {/* Neighbor Nodes */}
      {displayNodes.filter((n) => n.id !== centerNode?.id).length > 0 && (
        <div>
          <h4 className="text-white/70 text-sm mb-2">
            Connected Entities (
            {displayNodes.filter((n) => n.id !== centerNode?.id).length})
          </h4>
          <div className="grid grid-cols-1 gap-2 max-h-64 overflow-y-auto">
            {displayNodes
              .filter((n) => n.id !== centerNode?.id)
              .map((node) => (
                <NodeCard
                  key={node.id}
                  node={node}
                  isCenter={false}
                  isSelected={selectedNode === node.id}
                  onClick={() => handleNodeClick(node.id)}
                  onEdit={() => startEditEntity(node)}
                  onDelete={() => initiateDeleteEntity(node.id, node.label)}
                />
              ))}
          </div>
        </div>
      )}
    </div>
  );
}

function NodeCard({
  node,
  isCenter,
  isSelected,
  onClick,
  onEdit,
  onDelete,
}: {
  node: GraphNode;
  isCenter: boolean;
  isSelected: boolean;
  onClick: () => void;
  onEdit?: () => void;
  onDelete?: () => void;
}) {
  const color = ENTITY_COLORS[node.type] || ENTITY_COLORS.Source;

  return (
    <div
      className={`w-full p-3 rounded-xl transition-all ${
        isCenter ? "bg-white/20" : "bg-white/10 hover:bg-white/15"
      } ${isSelected ? "ring-2 ring-white" : ""}`}
    >
      <div className="flex items-center justify-between">
        <button
          onClick={onClick}
          className="flex items-center gap-2 flex-1 text-left"
        >
          <div
            className="w-3 h-3 rounded-full flex-shrink-0"
            style={{ backgroundColor: color }}
          />
          <div className="min-w-0">
            <p className="text-white font-medium text-sm truncate">{node.label}</p>
            <p className="text-white/50 text-xs">{node.type}</p>
          </div>
        </button>
        <div className="flex gap-1 ml-2">
          {onEdit && (
            <button
              onClick={(e) => {
                e.stopPropagation();
                onEdit();
              }}
              className="p-1 text-white/60 hover:text-white hover:bg-white/20 rounded transition-all"
              title="Edit entity"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
              </svg>
            </button>
          )}
          {onDelete && (
            <button
              onClick={(e) => {
                e.stopPropagation();
                onDelete();
              }}
              className="p-1 text-white/60 hover:text-red-400 hover:bg-white/20 rounded transition-all"
              title="Delete entity"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
              </svg>
            </button>
          )}
        </div>
      </div>
    </div>
  );
}

function EdgeCard({
  edge,
  fromNode,
  toNode,
}: {
  edge: GraphEdge;
  fromNode?: GraphNode;
  toNode?: GraphNode;
}) {
  const fromLabel = fromNode?.label || edge.from;
  const toLabel = toNode?.label || edge.to;
  const confidence = Math.round(edge.confidence * 100);

  return (
    <div className="bg-white/5 rounded-lg p-2 text-sm">
      <div className="flex items-center gap-2 text-white/80">
        <span className="truncate">{fromLabel}</span>
        <span className="text-white/40">→</span>
        <span className="text-white/60 italic flex-shrink-0">{edge.label}</span>
        <span className="text-white/40">→</span>
        <span className="truncate">{toLabel}</span>
      </div>
      {confidence < 100 && (
        <div className="text-white/40 text-xs mt-1">
          Confidence: {confidence}%
        </div>
      )}
    </div>
  );
}
