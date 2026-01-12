"use client";

import { CallCard } from "@/components/call-card";
import { SourceReferenceList } from "@/components/source-reference";
import { ContentDetailCard } from "@/components/content-detail";
import { PersonaSelector } from "@/components/persona-selector";
import { ContentUploadForm } from "@/components/content-upload";
import { ContentListView } from "@/components/content-list";
import { FamilyGraphView } from "@/components/family-graph-view";
import { AncestryReportCard } from "@/components/ancestry-report-card";
import {
  useCoAgent,
  useFrontendTool,
  useRenderToolCall,
} from "@copilotkit/react-core";
import { CopilotKitCSSProperties, CopilotSidebar } from "@copilotkit/react-ui";
import { useState, useCallback } from "react";
import { AgentState, AncestryReportToolResult, GraphRagSearchResult, GraphNeighborhood } from "@/lib/types";

// FamilyAI backend URL - used for REST API calls
const FAMILY_AI_API_URL =
  process.env.NEXT_PUBLIC_FAMILY_AI_API_URL || "http://localhost:8000";

// Default state to use when initializing or updating state
const DEFAULT_STATE: AgentState = {
  sessionId: undefined,
  currentPersona: undefined,
  selectedContentId: undefined,
  availablePersonas: ["Grandfather James", "Grandmother Mary", "Great Uncle John"],
  recentSearchResults: [],
  currentContentMetadata: undefined,
  currentGraphNeighborhood: undefined,
  fullGraph: undefined,
  graphRagContext: undefined,
};

export default function FamilyAIPage() {
  const [themeColor, setThemeColor] = useState("#4f46e5"); // Indigo theme for family/history feel
  const [activeView, setActiveView] = useState<
    "content" | "upload" | "persona" | "graph"
  >("content");

  // 🪁 Frontend Actions: https://docs.copilotkit.ai/microsoft-agent-framework/frontend-actions
  useFrontendTool({
    name: "setThemeColor",
    description: "Set the theme color of the application",
    parameters: [
      {
        name: "themeColor",
        type: "string",
        description: "The theme color to set. Pick warm, nostalgic colors.",
        required: true,
      },
    ],
    handler: async ({ themeColor }) => {
      setThemeColor(themeColor);
    },
  });

  // Frontend tool to switch views
  useFrontendTool({
    name: "switchView",
    description:
      "Switch the main content view between content list, upload form, persona selector, or knowledge graph",
    parameters: [
      {
        name: "view",
        type: "string",
        description:
          "The view to switch to: 'content' for content list, 'upload' for upload form, 'persona' for persona selector, 'graph' for knowledge graph",
        required: true,
      },
    ],
    handler: async ({ view }) => {
      if (view === "content" || view === "upload" || view === "persona" || view === "graph") {
        setActiveView(view);
      }
    },
  });

  return (
    <main
      style={
        { "--copilot-kit-primary-color": themeColor } as CopilotKitCSSProperties
      }
    >
      <CopilotSidebar
        disableSystemMessage={true}
        clickOutsideToClose={false}

        labels={{
          title: "Family Historian",
          initial:
            "👋 Hello! I'm your Family Historian assistant. I can help you explore your family history, search through documents and photos, and even speak as your ancestors. What would you like to discover today?",
        }}

        suggestions={[
          {
            title: "Search Family Content",
            message: "Search for photos of my grandparents",
          },
          {
            title: "Speak as Ancestor",
            message: "Let me speak with Grandfather James",
          },
          {
            title: "Start Video Call",
            message: "Start a video call with my family",
          },
          {
            title: "Upload Content",
            message: "I want to upload some family photos",
          },
          {
            title: "View Content",
            message: "Show me all my uploaded content",
          },
          {
            title: "Find Family Records",
            message: "Search for wedding records from the 1950s",
          },
        ]}
      >
        <FamilyAIContent
          themeColor={themeColor}
          activeView={activeView}
          setActiveView={setActiveView}
        />
      </CopilotSidebar>
    </main>
  );
}

function FamilyAIContent({
  themeColor,
  activeView,
  setActiveView,
}: {
  themeColor: string;
  activeView: "content" | "upload" | "persona" | "graph";
  setActiveView: (view: "content" | "upload" | "persona" | "graph") => void;
}) {
  // 🪁 Shared State: https://docs.copilotkit.ai/pydantic-ai/shared-state
  const { state, setState } = useCoAgent<AgentState>({
    name: "family_historian",
    initialState: {
      sessionId: undefined,
      currentPersona: undefined,
      selectedContentId: undefined,
      availablePersonas: [
        "Grandfather James",
        "Grandmother Mary",
        "Great Uncle John",
      ], // Sample personas
      recentSearchResults: [],
      currentContentMetadata: undefined,
      currentGraphNeighborhood: undefined,
      fullGraph: undefined,
      graphRagContext: undefined,
    },
  });

  // Callback for selecting content
  const handleSelectContent = useCallback(
    (contentId: string) => {
      setState((prev) => ({
        ...DEFAULT_STATE,
        ...prev,
        selectedContentId: contentId,
      }));
    },
    [setState]
  );

  // Callback for persona change
  const handlePersonaChange = useCallback(
    (persona: string | null) => {
      setState((prev) => ({
        ...DEFAULT_STATE,
        ...prev,
        currentPersona: persona || undefined,
      }));
    },
    [setState]
  );

  // Callback for graph node click - this could trigger agent to get neighborhood
  const handleGraphNodeClick = useCallback(
    (nodeId: string) => {
      // The node click can be used to focus on a specific entity
      // The agent tools will update the currentGraphNeighborhood in state
      console.log("Graph node clicked:", nodeId);
    },
    []
  );

  // Callback for adding relationship via agent tool
  const handleAddRelationship = useCallback(
    (fromId: string, toId: string, relationshipType: string) => {
      // This would typically trigger an agent tool call
      console.log("Add relationship:", fromId, relationshipType, toId);
    },
    []
  );

  // Callback for creating entity via agent tool
  const handleCreateEntity = useCallback(
    (name: string, entityType: string, attributes?: string) => {
      console.log("Create entity:", name, entityType, attributes);
    },
    []
  );

  // Callback for updating entity via agent tool
  const handleUpdateEntity = useCallback(
    (entityId: string, newName?: string, attributes?: string) => {
      console.log("Update entity:", entityId, newName, attributes);
    },
    []
  );

  // Callback for deleting entity via agent tool
  const handleDeleteEntity = useCallback(
    (entityId: string) => {
      console.log("Delete entity:", entityId);
    },
    []
  );

  // 🪁 Generative UI: Render search results
  useRenderToolCall(
    {
      name: "search_family_content",
      description: "Search family history content",
      parameters: [
        { name: "query", type: "string", required: true },
        { name: "maxResults", type: "number", required: false },
      ],
      render: ({ args }) => {
        return (
          <SourceReferenceList
            references={state.recentSearchResults}
            themeColor={themeColor}
            query={args.query}
            onSelect={handleSelectContent}
            selectedContentId={state.selectedContentId}
          />
        );
      },
    },
    [themeColor, state.recentSearchResults, state.selectedContentId]
  );

  // 🪁 Generative UI: Render content detail
  useRenderToolCall(
    {
      name: "get_content_metadata",
      description: "Get detailed information about content",
      parameters: [{ name: "contentId", type: "string", required: true }],
      render: () => {
        return (
          <ContentDetailCard
            metadata={state.currentContentMetadata}
            themeColor={themeColor}
            onClose={() =>
              setState((prev) => ({
                ...DEFAULT_STATE,
                ...prev,
                selectedContentId: undefined,
                currentContentMetadata: undefined,
              }))
            }
          />
        );
      },
    },
    [themeColor, state.currentContentMetadata]
  );

  // 🪁 Generative UI: Render persona selector
  useRenderToolCall(
    {
      name: "set_persona",
      description: "Set or change the current persona",
      parameters: [{ name: "personaName", type: "string", required: false }],
      render: () => {
        return (
          <PersonaSelector
            currentPersona={state.currentPersona}
            availablePersonas={state.availablePersonas}
            themeColor={themeColor}
            onPersonaChange={handlePersonaChange}
          />
        );
      },
    },
    [
      themeColor,
      state.currentPersona,
      state.availablePersonas,
      handlePersonaChange,
    ]
  );

  // 🎥 Video Call: Generative UI for starting video calls
  useRenderToolCall(
    {
      name: "start_video_call",
      description: "Start a video call with the specified contact or group.",
      parameters: [
        { name: "callId", type: "string", required: false },
        { name: "callTarget", type: "string", required: false },
      ],
      render: ({ args }) => {
        return (
          <CallCard
            themeColor={themeColor}
            callId={args.callId}
            callTarget={args.callTarget}
          />
        );
      },
    },
    [themeColor]
  );

  // 🕸️ Knowledge Graph: Generative UI for graph search results
  useRenderToolCall(
    {
      name: "search_knowledge_graph",
      description: "Search the family knowledge graph",
      parameters: [
        { name: "query", type: "string", required: true },
        { name: "maxResults", type: "number", required: false },
      ],
      render: () => {
        return (
          <FamilyGraphView
            neighborhood={state.currentGraphNeighborhood}
            onNodeClick={handleGraphNodeClick}
            onAddRelationship={handleAddRelationship}
            onCreateEntity={handleCreateEntity}
            onUpdateEntity={handleUpdateEntity}
            onDeleteEntity={handleDeleteEntity}
          />
        );
      },
    },
    [themeColor, state.currentGraphNeighborhood, handleGraphNodeClick, handleAddRelationship, handleCreateEntity, handleUpdateEntity, handleDeleteEntity]
  );

  // 🕸️ Knowledge Graph: Generative UI for entity neighborhood
  useRenderToolCall(
    {
      name: "get_entity_neighborhood",
      description: "Get the neighborhood of a specific entity",
      parameters: [{ name: "entityId", type: "string", required: true }],
      render: () => {
        return (
          <FamilyGraphView
            neighborhood={state.currentGraphNeighborhood}
            onNodeClick={handleGraphNodeClick}
            onAddRelationship={handleAddRelationship}
            onCreateEntity={handleCreateEntity}
            onUpdateEntity={handleUpdateEntity}
            onDeleteEntity={handleDeleteEntity}
          />
        );
      },
    },
    [themeColor, state.currentGraphNeighborhood, handleGraphNodeClick, handleAddRelationship, handleCreateEntity, handleUpdateEntity, handleDeleteEntity]
  );

  // 📜 Ancestry Reports: Generative UI for ancestry report generation
  useRenderToolCall(
    {
      name: "generate_ancestry_report",
      description: "Generate an ancestry report for a person",
      parameters: [
        { name: "personName", type: "string", required: true },
        { name: "maxGenerations", type: "number", required: false },
        { name: "style", type: "string", required: false },
      ],
      render: ({ result }) => {
        const reportResult = result as AncestryReportToolResult;
        return (
          <AncestryReportCard
            result={reportResult}
            themeColor={themeColor}
          />
        );
      },
    },
    [themeColor]
  );

  // 🔍 GraphRAG Local Search: Generative UI for entity-based search
  useRenderToolCall(
    {
      name: "search_graph_local",
      description: "Search the knowledge graph using Local mode",
      parameters: [
        { name: "query", type: "string", required: true },
        { name: "maxEntities", type: "number", required: false },
      ],
      render: ({ result }) => {
        const searchResult = result as GraphRagSearchResult;
        return (
          <GraphRagSearchCard
            result={searchResult}
            themeColor={themeColor}
            neighborhood={state.currentGraphNeighborhood}
            onNodeClick={handleGraphNodeClick}
          />
        );
      },
    },
    [themeColor, state.currentGraphNeighborhood, handleGraphNodeClick]
  );

  // 🌐 GraphRAG Global Search: Generative UI for community-based search
  useRenderToolCall(
    {
      name: "search_graph_global",
      description: "Search the knowledge graph using Global mode",
      parameters: [
        { name: "query", type: "string", required: true },
        { name: "maxEntities", type: "number", required: false },
      ],
      render: ({ result }) => {
        const searchResult = result as GraphRagSearchResult;
        return (
          <GraphRagSearchCard
            result={searchResult}
            themeColor={themeColor}
            neighborhood={state.currentGraphNeighborhood}
            onNodeClick={handleGraphNodeClick}
          />
        );
      },
    },
    [themeColor, state.currentGraphNeighborhood, handleGraphNodeClick]
  );

  // 📊 Load Full Graph: Generative UI for loading the complete graph
  useRenderToolCall(
    {
      name: "load_full_graph",
      description: "Load the user's complete knowledge graph",
      parameters: [],
      render: () => {
        return (
          <FamilyGraphView
            neighborhood={state.currentGraphNeighborhood}
            onNodeClick={handleGraphNodeClick}
            onAddRelationship={handleAddRelationship}
            onCreateEntity={handleCreateEntity}
            onUpdateEntity={handleUpdateEntity}
            onDeleteEntity={handleDeleteEntity}
            autoPreload={false}
          />
        );
      },
    },
    [themeColor, state.currentGraphNeighborhood, handleGraphNodeClick, handleAddRelationship, handleCreateEntity, handleUpdateEntity, handleDeleteEntity]
  );

  return (
    <div
      style={{ backgroundColor: themeColor }}
      className="min-h-screen flex flex-col items-center p-6 transition-colors duration-300"
    >
      {/* Header */}
      <div className="text-center mb-8">
        <h1 className="text-4xl font-bold text-white mb-2">
          Family History Explorer
        </h1>
        <p className="text-white/80 text-lg">
          Discover and connect with your family&apos;s past
        </p>
        {state.currentPersona && (
          <p className="text-white/90 text-sm mt-2 bg-white/20 inline-block px-4 py-1 rounded-full">
            Currently speaking as: <strong>{state.currentPersona}</strong>
          </p>
        )}
      </div>

      {/* View Toggle */}
      <div className="flex gap-2 mb-6 flex-wrap justify-center">
        <button
          onClick={() => setActiveView("content")}
          className={`px-4 py-2 rounded-xl text-white text-sm font-medium transition-all ${
            activeView === "content" ? "bg-white/30" : "bg-white/10 hover:bg-white/20"
          }`}
        >
          Content Library
        </button>
        <button
          onClick={() => setActiveView("upload")}
          className={`px-4 py-2 rounded-xl text-white text-sm font-medium transition-all ${
            activeView === "upload" ? "bg-white/30" : "bg-white/10 hover:bg-white/20"
          }`}
        >
          Upload
        </button>
        <button
          onClick={() => setActiveView("persona")}
          className={`px-4 py-2 rounded-xl text-white text-sm font-medium transition-all ${
            activeView === "persona" ? "bg-white/30" : "bg-white/10 hover:bg-white/20"
          }`}
        >
          Personas
        </button>
        <button
          onClick={() => setActiveView("graph")}
          className={`px-4 py-2 rounded-xl text-white text-sm font-medium transition-all ${
            activeView === "graph" ? "bg-white/30" : "bg-white/10 hover:bg-white/20"
          }`}
        >
          🕸️ Knowledge Graph
        </button>
      </div>

      {/* Main Content Area */}
      <div className="w-full max-w-md">
        {activeView === "content" && (
          <ContentListView
            themeColor={themeColor}
            apiBaseUrl={FAMILY_AI_API_URL}
            onSelectContent={handleSelectContent}
            selectedContentId={state.selectedContentId}
          />
        )}

        {activeView === "upload" && (
          <ContentUploadForm
            themeColor={themeColor}
            apiBaseUrl={FAMILY_AI_API_URL}
            onUploadSuccess={(contentId) => {
              handleSelectContent(contentId);
              setActiveView("content");
            }}
          />
        )}

        {activeView === "persona" && (
          <PersonaSelector
            currentPersona={state.currentPersona}
            availablePersonas={state.availablePersonas}
            themeColor={themeColor}
            onPersonaChange={handlePersonaChange}
          />
        )}

        {activeView === "graph" && (
          <FamilyGraphView
            neighborhood={state.currentGraphNeighborhood}
            onNodeClick={handleGraphNodeClick}
            onAddRelationship={handleAddRelationship}
            onCreateEntity={handleCreateEntity}
            onUpdateEntity={handleUpdateEntity}
            onDeleteEntity={handleDeleteEntity}
          />
        )}

        {/* Selected Content Detail */}
        {state.currentContentMetadata && (
          <ContentDetailCard
            metadata={state.currentContentMetadata}
            themeColor={themeColor}
            onClose={() =>
              setState((prev) => ({
                ...DEFAULT_STATE,
                ...prev,
                selectedContentId: undefined,
                currentContentMetadata: undefined,
              }))
            }
          />
        )}

        {/* Recent Search Results */}
        {state.recentSearchResults.length > 0 && (
          <SourceReferenceList
            references={state.recentSearchResults}
            themeColor={themeColor}
            onSelect={handleSelectContent}
            selectedContentId={state.selectedContentId}
          />
        )}
      </div>

      {/* Session Info */}
      {state.sessionId && (
        <div className="mt-8 text-white/40 text-xs">
          Session: {state.sessionId}
        </div>
      )}
    </div>
  );
}

// Component for displaying GraphRAG search results
function GraphRagSearchCard({
  result,
  neighborhood,
  onNodeClick,
}: {
  result: GraphRagSearchResult;
  themeColor: string;
  neighborhood?: GraphNeighborhood;
  onNodeClick?: (nodeId: string) => void;
}) {
  if (!result.success) {
    return (
      <div className="bg-white/10 backdrop-blur-sm rounded-2xl p-4 shadow-lg">
        <p className="text-white/70">{result.message}</p>
      </div>
    );
  }

  return (
    <div className="bg-white/10 backdrop-blur-sm rounded-2xl p-4 shadow-lg">
      <div className="flex items-center gap-2 mb-3">
        <span className="text-lg">{result.queryMode === "Global" ? "🌐" : "🔍"}</span>
        <h4 className="text-white font-semibold">
          {result.queryMode} Search Results
        </h4>
      </div>

      {/* Narrative Summary */}
      {result.narrativeSummary && (
        <div className="bg-white/10 rounded-lg p-3 mb-3">
          <p className="text-white/90 text-sm">{result.narrativeSummary}</p>
        </div>
      )}

      {/* Statistics */}
      <div className="flex gap-4 text-white/70 text-xs mb-3">
        <span>📊 {result.entityCount} entities</span>
        <span>🔗 {result.relationshipCount} relationships</span>
        {result.communityCount > 0 && (
          <span>🏘️ {result.communityCount} communities</span>
        )}
      </div>

      {/* Community Themes (for Global mode) */}
      {result.communityThemes && result.communityThemes.length > 0 && (
        <div className="mb-3">
          <p className="text-white/60 text-xs mb-1">Themes:</p>
          <div className="flex flex-wrap gap-1">
            {result.communityThemes.map((theme, idx) => (
              <span
                key={idx}
                className="px-2 py-0.5 bg-white/20 rounded text-white/80 text-xs"
              >
                {theme}
              </span>
            ))}
          </div>
        </div>
      )}

      {/* Mini Graph View */}
      {neighborhood && neighborhood.nodes.length > 0 && (
        <div className="mt-3 pt-3 border-t border-white/20">
          <p className="text-white/60 text-xs mb-2">Found Entities:</p>
          <div className="flex flex-wrap gap-2">
            {neighborhood.nodes.slice(0, 8).map((node) => (
              <button
                key={node.id}
                onClick={() => onNodeClick?.(node.id)}
                className="px-2 py-1 bg-white/20 hover:bg-white/30 rounded text-white text-xs transition-all"
                style={{
                  borderLeft: `3px solid ${
                    node.type === "Person"
                      ? "#4f46e5"
                      : node.type === "Place"
                      ? "#059669"
                      : node.type === "Event"
                      ? "#d97706"
                      : "#6b7280"
                  }`,
                }}
              >
                {node.label}
              </button>
            ))}
            {neighborhood.nodes.length > 8 && (
              <span className="px-2 py-1 text-white/50 text-xs">
                +{neighborhood.nodes.length - 8} more
              </span>
            )}
          </div>
        </div>
      )}

      <p className="text-white/50 text-xs mt-3">{result.message}</p>
    </div>
  );
}
