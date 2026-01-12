// State of the FamilyAI agent, synchronized via AG-UI and useCoAgent
export type AgentState = {
  sessionId?: string;
  currentPersona?: string;
  selectedContentId?: string;
  availablePersonas: string[];
  recentSearchResults: SourceReference[];
  currentContentMetadata?: ContentMetadataSummary;
  currentGraphNeighborhood?: GraphNeighborhood;
  fullGraph?: FullGraphState;
  graphRagContext?: GraphRagContextState;
};

// Source reference from content search
export type SourceReference = {
  contentId: string;
  title?: string;
  contentType: ContentType;
  relevanceScore: number;
};

// Content type enum matching the backend
export type ContentType =
  | "Document"
  | "DocumentImage"
  | "Photo"
  | "Video"
  | "Audio";

// Processing status enum matching the backend
export type ProcessingStatus =
  | "Pending"
  | "Processing"
  | "Completed"
  | "Failed";

// Content metadata summary
export type ContentMetadataSummary = {
  contentId: string;
  title?: string;
  contentType: ContentType;
  summary?: string;
  familyMembers: string[];
  status: ProcessingStatus;
};

// Graph node representing an entity in the knowledge graph
export type GraphNode = {
  id: string;
  label: string;
  type: GraphEntityType;
};

// Graph entity type enum matching the backend
export type GraphEntityType = "Person" | "Place" | "Event" | "Source";

// Graph edge representing a relationship between entities
export type GraphEdge = {
  id: string;
  from: string;
  to: string;
  label: string;
  confidence: number;
};

// Graph neighborhood containing a center node with its neighbors and edges
export type GraphNeighborhood = {
  centerNode?: GraphNode;
  nodes: GraphNode[];
  edges: GraphEdge[];
};

// Full graph state for preloading
export type FullGraphState = {
  nodes: GraphNode[];
  edges: GraphEdge[];
  communitySummaries: CommunitySummaryState[];
  loadedAt: string;
};

// Community summary in the agent state
export type CommunitySummaryState = {
  communityId: string;
  name?: string;
  summaryText?: string;
  themes: string[];
  keyPeople: string[];
};

// GraphRAG context from a search operation
export type GraphRagContextState = {
  queryMode: "Local" | "Global";
  narrativeSummary?: string;
  relevantEntityNames: string[];
  sourceContentIds: string[];
  communityThemes: string[];
};

// Full graph response from the API
export type FullGraphResponse = {
  nodes: GraphNode[];
  edges: GraphEdge[];
  communitySummaries: CommunitySummaryState[];
  loadedAt: string;
};

// GraphRAG search result
export type GraphRagSearchResult = {
  success: boolean;
  queryMode: string;
  narrativeSummary?: string;
  entityCount: number;
  relationshipCount: number;
  communityCount: number;
  communityThemes: string[];
  sourceContentIds: string[];
  message: string;
};

// Full family content model (for REST API responses)
export type FamilyContent = {
  id: string;
  userId: string;
  contentType: ContentType;
  fileName: string;
  mimeType: string;
  fileSizeBytes: number;
  storageUrl?: string;
  status: ProcessingStatus;
  title?: string;
  description?: string;
  contentDate?: string;
  familyMembers: string[];
  tags: string[];
  uploadedAt: string;
  modifiedAt: string;
};

// Processed content model (for REST API responses)
export type ProcessedContent = {
  id: string;
  userId: string;
  contentId: string;
  extractedText?: string;
  summary?: string;
  entities: ExtractedEntity[];
  embedding?: number[];
  sentiment?: string;
  keywords: string[];
  detectedLanguage?: string;
  processedAt: string;
  modelVersion?: string;
  processingErrors: string[];
};

// Extracted entity from AI processing
export type ExtractedEntity = {
  type: string;
  value: string;
  confidence: number;
  context?: string;
};

// Content list response from REST API
export type ContentListResponse = {
  items: FamilyContent[];
  totalCount: number;
  continuationToken?: string;
};

// Content response with processed content
export type ContentResponse = {
  content: FamilyContent;
  processedContent?: ProcessedContent;
};

// Ancestry report payload containing structured report sections
export type AncestryReportPayload = {
  title?: string;
  overview?: string;
  familyTreeNarrative?: string;
  keyEvents: string[];
  notableRelationships: string[];
  dataGaps: string[];
  researchSuggestions: string[];
};

// Full ancestry report model
export type AncestryReport = {
  id: string;
  userId: string;
  focusEntityId: string;
  focusPersonName: string;
  title?: string;
  overview?: string;
  familyTreeNarrative?: string;
  keyEvents: string[];
  notableRelationships: string[];
  dataGaps: string[];
  researchSuggestions: string[];
  linkedEntityIds: string[];
  sourceContentIds: string[];
  createdAt: string;
  modifiedAt: string;
  modelVersion?: string;
};

// Result from the ancestry report generation tool
export type AncestryReportToolResult = {
  success: boolean;
  report?: AncestryReport;
  message: string;
};

// Summary of an ancestry report for listing
export type AncestryReportSummary = {
  id: string;
  focusPersonName: string;
  title: string;
  createdAt: string;
};

// Result from listing ancestry reports
export type ListAncestryReportsResult = {
  success: boolean;
  reports: AncestryReportSummary[];
  message: string;
};