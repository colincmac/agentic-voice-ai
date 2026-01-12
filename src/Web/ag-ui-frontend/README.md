# FamilyAI - Family History Explorer

This is a Next.js application powered by [CopilotKit](https://copilotkit.ai) and the [Microsoft Agent Framework](https://github.com/microsoft/agents) AG-UI protocol. It provides a conversational interface for exploring family history through documents, photos, videos, and audio recordings.

## FamilyAI AG-UI Integration

This frontend connects to the `Showcase.Agent.FamilyAI` ASP.NET Core backend via the AG-UI protocol, enabling:

### Features

- **🔄 Shared State**: Session ID, current persona, and selected content are synchronized between the frontend and backend agent
- **🔍 Family Content Search**: Search through processed family documents with Generative UI rendering of results
- **👤 Persona Impersonation**: "Speak as" specific ancestors with persona-aware responses
- **📄 Content Management**: Upload, view, and manage family documents, photos, videos, and audio via REST APIs
- **🎥 Video Calling**: Azure Communication Services integration for family video calls
- **🛠️ Frontend Actions**: Theme customization and view switching controlled by the agent

### Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  agentic-family-ui (Next.js)                                │
│  ├─ CopilotKit React Components                             │
│  │   ├─ CopilotSidebar (chat UI)                            │
│  │   ├─ useCoAgent (shared state)                           │
│  │   ├─ useRenderToolCall (Generative UI)                   │
│  │   └─ useFrontendTool (frontend actions)                  │
│  │                                                          │
│  ├─ FamilyAI Components                                     │
│  │   ├─ SourceReferenceList (search results)                │
│  │   ├─ ContentDetailCard (content viewer)                  │
│  │   ├─ PersonaSelector (ancestor selection)                │
│  │   ├─ ContentUploadForm (file upload via REST)            │
│  │   └─ ContentListView (content library via REST)          │
│  │                                                          │
│  └─ API Routes                                              │
│      ├─ /api/copilotkit (AG-UI proxy to backend)            │
│      └─ /api/acs-token (ACS token generation)               │
└──────────────────────────┬──────────────────────────────────┘
                           │ AG-UI Protocol
                           ▼
┌─────────────────────────────────────────────────────────────┐
│  Showcase.Agent.FamilyAI (ASP.NET Core)                     │
│  ├─ AG-UI Endpoint (/)                                      │
│  │   ├─ FamilyAITools (search, persona, session)            │
│  │   ├─ CallingTools (video call initiation)                │
│  │   └─ FamilyAIAgentState (shared state)                   │
│  │                                                          │
│  └─ REST API (/api/...)                                     │
│      ├─ /api/content (CRUD operations)                      │
│      └─ /api/chat (direct chat interface)                   │
└─────────────────────────────────────────────────────────────┘
```

### AG-UI Tools

The backend exposes these tools through the AG-UI protocol:

| Tool | Description | Generative UI |
|------|-------------|---------------|
| `search_family_content` | Search processed content by query | SourceReferenceList |
| `get_content_metadata` | Get detailed content information | ContentDetailCard |
| `set_persona` | Set/clear ancestor impersonation | PersonaSelector |
| `set_session` | Manage conversation sessions | - |
| `select_content` | Focus on specific content in UI | - |
| `show_agent_state` | Debug current agent state | - |
| `start_video_call` | Initiate ACS video call | CallCard |

### Shared State

The following state is synchronized via `useCoAgent`:

```typescript
type AgentState = {
  sessionId?: string;           // Current conversation session
  currentPersona?: string;      // Active ancestor persona
  selectedContentId?: string;   // Focused content in UI
  availablePersonas: string[];  // Discovered ancestors
  recentSearchResults: SourceReference[];
  currentContentMetadata?: ContentMetadataSummary;
};
```

## Prerequisites

- **GitHub Personal Access Token** (for GitHub Models API)
  - Retrieve from GitHub using [these instructions](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#creating-a-personal-access-token-classic).
  - or generate via `gh auth token` in your CLI (requires [GitHub CLI](https://github.com/cli/cli?tab=readme-ov-file#installation))
- **.NET 9.0 SDK**
  - [Download directly](https://dotnet.microsoft.com/download/dotnet/9.0)
  - macOS/Linux
    - [Install via Homebrew](https://formulae.brew.sh/formula/dotnet) (`brew install dotnet@9`) or
    - <details><summary>Install via <code>curl</code> install script</summary><br />

      ```bash
      curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 9.0
      export PATH="$HOME/.dotnet:$PATH"
      ```

      </details>
  - Windows
    - [Install via WinGet](https://winstall.app/apps/Microsoft.DotNet.SDK.9) (`winget install --id=Microsoft.DotNet.SDK.9 -e`)
- **Node.js 20+**
  - [Download directly](https://nodejs.org/en/download)
  - macOS/Linux
    - [Install via Homebrew](https://formulae.brew.sh/formula/node@24) (`brew install node@24`) or
    - <details><summary>Install via <code>curl</code> install script</summary><br />

      ```bash
      # Download and install nvm:
      curl -o- https://raw.githubusercontent.com/nvm-sh/nvm/v0.40.3/install.sh | bash

      # in lieu of restarting the shell
      \. "$HOME/.nvm/nvm.sh"

      # Download and install Node.js:
      nvm install 24
      ```

      </details>
  - Windows
    - [Install via WinGet](https://winstall.app/apps/OpenJS.NodeJS) (`winget install --id=OpenJS.NodeJS -v "24.11.0" -e`)
- Any of the following package managers:
  - [pnpm](https://pnpm.io/installation) **(recommended)**
  - [npm](https://docs.npmjs.com/downloading-and-installing-node-js-and-npm) (usually installed with Node.js)
  - [yarn](https://yarnpkg.com/getting-started/install)
  - [bun](https://bun.com/docs/installation)

> **Note:** This repository ignores lock files (package-lock.json, yarn.lock, pnpm-lock.yaml, bun.lockb) to avoid conflicts between different package managers. Each developer should generate their own lock file using their preferred package manager. After that, make sure to delete it from the .gitignore.

## Getting Started with FamilyAI

### Running with the FamilyAI Backend

To run the full FamilyAI experience with the AG-UI backend:

1. **Start the FamilyAI Backend** (from the repository root):

    ```bash
    cd src/Agents/Showcase.Agent.FamilyAI
    dotnet run
    ```

    This starts the ASP.NET Core backend on `http://localhost:8000` with:
    - AG-UI endpoint at `/` for CopilotKit integration
    - REST API at `/api/content` and `/api/chat`

2. **Configure Environment Variables** (optional):

    Copy the example environment file and customize as needed:
    ```bash
    cp .env.local.example .env.local
    ```

    Environment variables:
    - `FAMILY_AI_AGUI_URL` - Backend AG-UI URL (default: `http://localhost:8000/`)
    - `NEXT_PUBLIC_FAMILY_AI_API_URL` - Backend REST API URL (default: `http://localhost:8000`)
    - `AZURE_COMMUNICATION_SERVICES_CONNECTION_STRING` - For video calling features

3. **Start the Frontend**:

    ```bash
    # Using pnpm (recommended)
    pnpm install
    pnpm dev:ui

    # Using npm
    npm install
    npm run dev:ui
    ```

    The frontend will be available at `http://localhost:3000`.

### Running with the Demo Agent

For development without the FamilyAI backend:

1. Install dependencies using your preferred package manager:

    ```bash
    # Using pnpm (recommended)
    pnpm install

    # Using npm
    npm install

    # Using yarn
    yarn install

    # Using bun
    bun install
    ```

    > **Note:** This will automatically setup the C# agent as well (restore NuGet packages).
    >
    > If you have manual issues, you can run:
    >
    > ```sh
    > npm run install:agent
    > ```

2. Set up your GitHub token for GitHub Models:

    First, get your GitHub token:
    ```bash
    gh auth token
    ```

    Then, navigate to the agent directory and set it as a user secret:
    ```bash
    cd agent
    dotnet user-secrets set GitHubToken "<your-token>"
    cd ..
    ```

    Or set it in one command:
    ```bash
    cd agent; dotnet user-secrets set GitHubToken "$(gh auth token)"; cd ..
    ```


3. Start the development server:

    ```bash
    # Using pnpm
    pnpm dev

    # Using npm
    npm run dev

    # Using yarn
    yarn dev

    # Using bun
    bun run dev
    ```

    This will start both the Next.js UI (port 3000) and C# agent server (port 8000) concurrently.

## Available Scripts
The following scripts can also be run using your preferred package manager:
- `dev` - Starts both UI and agent servers in development mode
- `dev:debug` - Starts development servers with debug logging enabled
- `dev:ui` - Starts only the Next.js UI server
- `build` - Builds the Next.js application for production
- `start` - Starts the production server
- `lint` - Runs ESLint for code linting

## Project Structure

```
├── src/
│   ├── app/
│   │   ├── page.tsx           # FamilyAI main page with sidebar and content views
│   │   ├── layout.tsx         # CopilotKit provider with family_historian agent
│   │   └── api/
│   │       ├── copilotkit/
│   │       │   └── route.ts   # AG-UI integration to FamilyAI backend
│   │       └── acs-token/
│   │           └── route.ts   # ACS token generation for video calling
│   ├── components/
│   │   ├── source-reference.tsx  # Search results list component
│   │   ├── content-detail.tsx    # Content metadata viewer
│   │   ├── persona-selector.tsx  # Ancestor persona picker
│   │   ├── content-upload.tsx    # File upload form (REST)
│   │   ├── content-list.tsx      # Content library view (REST)
│   │   └── call-card.tsx         # Video call component
│   └── lib/
│       └── types.ts              # FamilyAI TypeScript types
├── .env.local.example            # Environment variable template
└── agent/                        # Demo C# Agent (optional)
```

## Features Demonstrated

This application showcases key AG-UI protocol features for family history exploration:

- **🔄 Shared State**: Session, persona, and content selection synchronized via `useCoAgent`
- **🔍 Content Search**: AI-powered search with Generative UI result cards
- **👤 Persona Impersonation**: "Speak as" specific ancestors from family history
- **📄 Document Management**: Upload and browse family documents, photos, videos, audio
- **📹 Video Calling**: Azure Communication Services integration for family calls
- **🛠️ Frontend Actions**: Theme customization and view switching via agent

## Video Calling with Azure Communication Services

This app includes video/voice calling capabilities powered by Azure Communication Services (ACS).

### Setting Up ACS

1. **Create an ACS Resource** in the Azure Portal:
   - Go to [Azure Portal](https://portal.azure.com)
   - Create a new "Communication Services" resource
   - Once created, go to "Keys" and copy the connection string

2. **Configure the Frontend**:
   Create a `.env.local` file in the `src/Web/agentic-family-ui` directory:
   ```env
   AZURE_COMMUNICATION_SERVICES_CONNECTION_STRING="endpoint=https://your-acs-resource.communication.azure.com/;accesskey=YOUR_ACCESS_KEY"
   ```

3. **Configure the Backend** (optional, for advanced scenarios):
   In the `Showcase.Agent.FamilyAI` project, set user secrets:
   ```bash
   cd src/Agents/Showcase.Agent.FamilyAI
   dotnet user-secrets set "AcsCalling:ConnectionString" "endpoint=https://your-acs-resource.communication.azure.com/;accesskey=YOUR_ACCESS_KEY"
   ```

### Using Video Calling

Try these prompts in the chat:
- "Start a video call"
- "Start a video call with my family"
- "I want to call Mom"

The agent will invoke the `start_video_call` tool, which renders a CallCard component with:
- Camera/microphone permission requests
- Local video preview
- Join/Start Call and Hang Up controls

### Teams Phone Integration (Advanced)

For PSTN calling via Teams Phone:
1. Set up a Teams Phone license and resource account
2. Configure the `AcsPhoneNumber` in the backend
3. Link your ACS resource to Teams Phone

Refer to the [Azure Communication Services documentation](https://docs.microsoft.com/azure/communication-services/) for detailed setup instructions.

## Operator Dashboard

The operator dashboard provides real-time monitoring of live AI voice calls. It connects to the VoiceAgent API backend via REST and SignalR.

### Accessing the Dashboard

Navigate to `/operator/calls` to access the operator dashboard.

### Configuration

Set the API URL in your `.env.local` file:
```env
NEXT_PUBLIC_VOICE_AGENT_API_URL=http://localhost:5000
```

### Features

- **Live call table**: View all active calls with customer, agent(s), status, duration, and health metrics
- **Real-time updates**: SignalR connection provides instant updates when calls start, end, or health metrics change
- **Sortable columns**: Sort by duration or escalation risk
- **Detailed call view**: Click on a call to see detailed health metrics, participants, active tasks, and latest utterance
- **Health indicators**: Color-coded badges for sentiment (green/amber/red), task adherence, and escalation risk

### Health Metric Thresholds

| Metric | Green | Amber | Red |
|--------|-------|-------|-----|
| Customer Sentiment | > 0.3 | -0.3 to 0.3 | < -0.3 |
| Agent Sentiment | > 0.3 | -0.3 to 0.3 | < -0.3 |
| Task Adherence | > 0.7 | 0.4 to 0.7 | < 0.4 |
| Escalation Risk | < 0.3 | 0.3 to 0.6 | > 0.6 |

For more details about the API, see [OPERATOR_DASHBOARD_API.md](../../Agents/Showcase.Agent.VoiceAgent/OPERATOR_DASHBOARD_API.md).

## 📚 Documentation

- [Microsoft Agent Framework](https://github.com/microsoft/agents) - Learn about Microsoft's agent framework
- [AG-UI Protocol](https://github.com/copilotkit/ag-ui) - AG-UI protocol specification
- [CopilotKit Documentation](https://docs.copilotkit.ai) - CopilotKit features and API
- [Next.js Documentation](https://nextjs.org/docs) - Next.js features and API
- [GitHub Models](https://github.com/marketplace/models) - Free AI models via GitHub

## Contributing

Feel free to submit issues and enhancement requests! This starter is designed to be easily extensible.

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Troubleshooting

### Agent Connection Issues
If you see "I'm having trouble connecting to my tools", make sure:
1. The C# agent is running on port 8000
2. Your GitHub token is set correctly via user secrets
3. Both servers started successfully (check terminal output)

### .NET SDK Not Installed
If you don't have .NET 9.0 installed:

**macOS/Linux (Homebrew):**
```bash
brew install dotnet@9
dotnet --version
```

**macOS/Linux (Install Script):**
```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 9.0
export PATH="$HOME/.dotnet:$PATH"
```

**Windows (WinGet):**
```powershell
winget install --id=Microsoft.DotNet.SDK.9 -e
```

**Windows/macOS (Direct Download):**
- Visit https://dotnet.microsoft.com/download/dotnet/9.0
- Download and run the installer

### .NET SDK Issues
If you encounter .NET-related errors:
```bash
# Verify .NET SDK is installed
dotnet --version  # Should be 9.0.x or higher

# Restore packages manually
cd agent
dotnet restore
dotnet run
```

### GitHub Token Issues
If the agent fails to start with "GitHubToken not found":
```bash
cd agent
dotnet user-secrets set GitHubToken "$(gh auth token)"
```

Or manually:
```bash
# Get your token
gh auth token

# Set it as a user secret
cd agent
dotnet user-secrets set GitHubToken "YOUR_TOKEN_HERE"
```

### Port Conflicts
If port 8000 is already in use, you can change it in:
- `agent/Properties/launchSettings.json` - Update `applicationUrl`
- `src/app/api/copilotkit/route.ts` - Update the HttpAgent URL
