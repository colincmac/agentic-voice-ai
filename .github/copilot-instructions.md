# Copilot Instructions

## Build, Test, Lint

The solution (`Showcase.slnx`) includes Python and M365 projects that require Visual Studio-specific tooling, so **prefer targeted builds** for the project you're changing rather than building the whole solution:

```shell
dotnet build src/AgentFramework/Agents.AI/Agents.AI.csproj
```

Run tests for a specific project:

```shell
dotnet test test/Agents.AI.Extensions.Tests/Agents.AI.Extensions.Tests.csproj
```

Run a single test by name (`--filter` goes before `--`):

```shell
dotnet test test/Agents.AI.Extensions.Tests/Agents.AI.Extensions.Tests.csproj --no-build --filter "State_InitializedWithDefaults"
```

Test projects (under `test/`):

- `AI.TestingFramework` — shared test helpers (ASP.NET fakes, WebSocket mocks, protocol fakes). Auto-referenced by all other test projects via `test/Directory.Build.targets`.
- `Agents.AI.Extensions.Tests` — extensions, AG-UI, session management, tool approval, etc.
- `Agents.AI.ContactCenter.Tests` — calling, IVR workflow, caller-auth, composite tier fallback.
- `Showcase.Authentication.Tests` — JWT/DPoP authentication.
- `Showcase.EnterpriseMcp.Tests` — MCP server and tools.

Frontend (`src/Web/ag-ui-frontend/`, uses pnpm):

```shell
pnpm install && pnpm run build
pnpm run lint
```

Python voice-biometrics service (`src/python-services/voice-biometrics/`, uses uv):

```shell
uv sync
uv run python -m pytest
```

## Architecture

Multi-service AI agent platform orchestrated by .NET Aspire. The canonical solution file is `Showcase.slnx`.

**Agent Framework** (`src/AgentFramework/`) — Reusable libraries for building real-time AI agents:

- `Agents.AI` — Core realtime agent abstractions: `IRealtimeAgent`, `RealtimeAIAgent`, `RealtimeAIAgentSession`, decorator base (`DelegatingRealtimeAIAgent`), and hosting extensions (`AddRealtimeAIAgent`, `AddKeyedConversationClient`, `AddKeyedChatClient`).
- `Extensions.AI` — `Microsoft.Extensions.AI`-style abstractions: `ILiveConversationClient`, `ILiveConversationSession`, `DelegatingConversationClient`, `LiveConversationClientBuilder`, audio helpers, content types, OpenTelemetry instrumentation.
- `Agents.AI.Extensions` — Higher-level building blocks layered on `Agents.AI`: AG-UI integration, AI tools, agent authorization, sensitive-data redaction, session management, tool approval, realtime agent helpers (`AuthorizingRealtimeAIAgent`).
- `Agents.AI.ContactCenter` — Contact-center features: call automation (`ICallSession`, `ICallControl`, `ICallSessionFactory`), tier strategies (DTMF / Intent NLU / Realtime voice / composite fallback), IVR workflow engine (YAML-driven `RealtimeIvrWorkflowDefinition`), caller authentication (ANI lookup, PIN validation), Azure Speech integration, telemetry, presence detection, and media analysis.
- `Agents.AI.RealtimeVoice.Teams` — Teams channel adapter and Adaptive Card helpers for hosting an agent inside Teams.

> Note: `Agents.AI.Extensions.LiveVoice` and `Agents.AI.RealtimeVoice.Azure` are no longer standalone projects — their proto files (`agents.realtimevoice.v1.proto`, `biometrics.v1.proto`) live under `src/AgentFramework/Agents.AI.RealtimeVoice.Azure/Protos/` and are surfaced in the solution under `Solution Items/protos/`. Do not add new code to these directories without first introducing a project.

**Agents** (`src/Agents/`) — Runnable agent services that compose the framework:

- `Showcase.Agent.VoiceAgent` — Full voice agent. Wires chat / realtime / voicelive conversation clients, Azure Speech, the YAML IVR workflow framework, an in-memory caller directory + PIN tools, the call-session container with composite tier fallback (`RealtimeVoice → IntentNlu → DtmfOnly`), and call automation endpoints. Talks to `IntentAgent` over gRPC when its connection string is available, otherwise falls back to an in-process stub classifier.
- `Showcase.Agent.IntentAgent` — Lightweight gRPC SLM intent-classification service. Deployed to the GPU compute environment (`aca-gpu-env`) per `docs/architecture/aks-topology.md` so the voice-edge stays on the CPU pool.

**Enterprise MCP** (`src/enterprise-mcp/`) — Model Context Protocol server (`Showcase.EnterpriseMcp.Server`) with attribute-based tool registration (`[McpServerToolType]`, `[McpServerTool]`), plus a `TestApp` client.

**Playground** (`src/Playground/`) — `Agents.AI.Playground.ConsoleApp` and `Showcase.ConsolePlayground` for exercising the framework outside of Aspire.

**Shared** (`src/Shared/`):

- `Showcase.AppHost` — Aspire orchestrator (entry: `Program.cs`). Currently wires `voiceagent` and `intentagent` with Cosmos DB (`ContactCenter` database), Azure Managed Redis, Key Vault, App Configuration, Application Insights, and AI model connection strings (`chat`, `embedding`, `realtime`, `voicelive`, `azurespeech`, `voicebiometrics`). Container apps target two ACA environments: a CPU env (`aca-env`) and a GPU env (`aca-gpu-env`). The Python biometrics service and frontend are checked in but currently commented out in the AppHost.
- `Showcase.Authentication` — JWT Bearer + DPoP authentication with Azure AD and Azure Key Vault key resolution; also includes MCP-specific auth helpers.
- `Showcase.ServiceDefaults` — OpenTelemetry, health checks, HTTP resilience, service discovery.
- `HostingExtensions/`, `Throw/` — shared source files injected into framework projects via `InjectHostingExtensions` / `InjectSharedThrow` MSBuild flags in `Directory.Build.props`.

**M365 Agent** (`M365Agent/`) — Teams Toolkit (`.atkproj`) project for hosting the agent in Microsoft 365. Requires Visual Studio tooling.

**Frontend** (`src/Web/ag-ui-frontend/`) — Next.js 15 / React 19 app with CopilotKit, Azure Communication Services, and SignalR.

**Python Services** (`src/python-services/voice-biometrics/`) — gRPC speaker-verification service using SpeechBrain/PyTorch. Built with `uv`; protos live in `protos/` and are generated via `build_protos.py`.

## Key Patterns

### Decorator/pipeline composition

Agents, conversation clients, and chat clients are composed using a decorator pipeline. Cross-cutting concerns (telemetry, auth, function invocation) are added by wrapping inner implementations:

```csharp
builder.AddKeyedConversationClient("voicelive")
    .UseFunctionInvocation()
    .UseOpenTelemetry(sourceName: "Showcase.VoiceAgent");

builder.AddRealtimeAIAgent(
    name: AgentConfig.TriageAgent,
    configurationSection: builder.Configuration.GetSection($"{AgentConfig.SectionName}:{AgentConfig.TriageAgent}"),
    liveConversationClientKey: "voicelive");
```

To add a new decorator, inherit from `DelegatingRealtimeAIAgent` (agents) or `DelegatingConversationClient` (conversation clients).

### Keyed DI services

Multiple agents and clients are registered using keyed DI (`AddKeyedSingleton`, `GetRequiredKeyedService`) to support multi-agent scenarios within a single host.

### Configuration binding

Agent behavior is driven by configuration sections bound to options objects (`RealtimeAgentOptions`, `LiveConversationSessionOptions`).

### MCP tool registration

```csharp
[McpServerToolType]
public class WeatherTools
{
    [McpServerTool, Description("Get weather alerts for a US state")]
    public async Task<string> GetAlerts(
        HttpClient client,
        [Description("Two-letter state code")] string state) { ... }
}
```

### Aspire AppHost wiring

When adding a new service, register it in `src/Shared/Showcase.AppHost/Program.cs` with `builder.AddProject<Projects.YourProject>("name")` and wire dependencies with `.WithReference(...)`. Pick the appropriate compute environment (`acaEnvironment` for CPU workloads, `acaGpuEnvironment` for GPU/model-inference workloads) via `.WithComputeEnvironment(...)`. Azure resources are bound to existing infrastructure with `.AsExisting(nameParam, resourceGroupParam)`; the parameter names live in `ParameterNameConstants`.

### Tiered call sessions (VoiceAgent)

`Showcase.Agent.VoiceAgent` composes a tier-based call session container:

```csharp
builder.AddCallSessionContainer()
    .AddRealtimeVoiceStrategy(realtimeAgentServiceKey: AgentConfig.TriageAgent)
    .AddNluStrategy()
    .AddDtmfStrategy()
    .AddCallControlTools()
    .AddCallerAuthentication()
    .AddCallerAuthenticator<AniIdentityLookupAuthenticator>()
    .AddTransferEscalationTarget(ShowcaseWorkflowIds.DefaultEscalationNumber)
    .AddCompositeFallbackStrategy(
        topTier: AgentTier.RealtimeVoice,
        AgentTier.RealtimeVoice,
        AgentTier.IntentNlu,
        AgentTier.DtmfOnly);
```

Per-call `IvrWorkflowState` and `CallerAuthenticationState` are preserved across mid-call tier swaps. When adding a new tier or strategy, follow the existing `IConversationStrategy` / `ICallSessionFactory` pattern and register it BEFORE the composite (last-wins resolution).

### IVR workflows

Workflows are declarative YAML files loaded by `AddIvrWorkflowFramework(...)`. The samples live under `src/Agents/Showcase.Agent.VoiceAgent/Workflow/Samples/*.yaml` and are copied to the app output via the csproj content glob. Tools referenced from YAML (`pin-validator`, `confirm-identity`, `transfer-to-agent`, ...) are registered with `.AddTool("name", sp => ...)`.

## Testing

- xUnit v3 with `Microsoft.Testing.Platform`
- All test projects auto-reference `AI.TestingFramework` (shared ASP.NET fakes, WebSocket mocks, protocol fakes)
- Snapshot testing with Verify is available (`dotnet verify accept -y` to accept changes)
- Do not use `Directory.SetCurrentDirectory` in tests — it causes concurrency issues

## C# Conventions

- .NET 10 (SDK pinned in `global.json` to `10.0.203`, `rollForward: latestMajor`, prerelease allowed) with C# preview features (`LangVersion: preview`); `Nullable` and `ImplicitUsings` enabled globally.
- `TreatWarningsAsErrors` is enabled — if temporarily needed during refactoring, add `/p:TreatWarningsAsErrors=false` but fix all warnings before finishing.
- NuGet versions are managed centrally:
  - `Directory.Packages.props` (root) for src projects.
  - `src/AgentFramework/Directory.Packages.props` for framework-specific pinning.
  - `test/Directory.Packages.props` for test-only packages.
  Do not add `Version` attributes in individual `.csproj` files.
- Shared source files are linked into framework projects via MSBuild flags in `Directory.Build.props`: set `<InjectSharedThrow>true</InjectSharedThrow>` to pull in `src/Shared/Throw/*.cs`, and `<InjectHostingExtensions>true</InjectHostingExtensions>` for `src/Shared/HostingExtensions/*.cs`.
- Private fields: `_camelCase`; constants: `PascalCase`; file-scoped namespaces; `var` preferred everywhere.
- Formatting is defined in `.editorconfig` — Allman-style braces, 4-space indent for C#, 2-space for XML/JSON.
- Never change `global.json` unless explicitly asked.
