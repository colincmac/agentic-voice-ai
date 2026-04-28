# Copilot Instructions

## Build, Test, Lint

The solution includes Python and M365 projects that require Visual Studio-specific tooling, so **prefer targeted builds** for the project you're changing:

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

Multi-service AI agent platform orchestrated by .NET Aspire.

**Agent Framework** (`src/AgentFramework/`) — Reusable libraries for building real-time AI agents:

- `Agents.AI` — Core abstractions: `IRealtimeAgent`, `RealtimeAIAgent`, decorator base classes (`DelegatingRealtimeAIAgent`)
- `Extensions.AI` — Voice/conversation abstractions: `ILiveConversationClient`, `ILiveConversationSession`, `LiveConversationClientBuilder`
- `Extensions.LiveVoice` — Live voice features including IVR workflow engine
- `RealtimeVoice.Azure` / `RealtimeVoice.Teams` — Platform-specific conversation client implementations

**Agents** (`src/Agents/`) — Runnable agent services that compose the framework:

- `VoiceAgent` — Full voice agent with call automation, multiple AI providers, and tool collections
- `IntentAgent` — Lightweight intent recognition service

**Enterprise MCP** (`src/enterprise-mcp/`) — Model Context Protocol server with attribute-based tool registration (`[McpServerToolType]`, `[McpServerTool]`).

**Shared** (`src/Shared/`):

- `Showcase.AppHost` — Aspire orchestrator. Currently wires VoiceAgent and IntentAgent with Cosmos DB, Redis, Key Vault, Application Insights, and AI model connection strings. Other services (Python biometrics, frontend) exist in the repo but are not yet wired into the AppHost.
- `Showcase.Authentication` — JWT Bearer + DPoP authentication with Azure AD and Key Vault
- `Showcase.ServiceDefaults` — OpenTelemetry, health checks, HTTP resilience, service discovery

**Frontend** (`src/Web/ag-ui-frontend/`) — Next.js 15 / React 19 app with CopilotKit, Azure Communication Services, and SignalR.

**Python Services** (`src/python-services/voice-biometrics/`) — gRPC speaker verification service using SpeechBrain/PyTorch.

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

When adding a new service, register it in `src/Shared/Showcase.AppHost/Program.cs` with `builder.AddProject<Projects.YourProject>("name")` and wire dependencies with `.WithReference(...)`.

## Testing

- xUnit v3 with `Microsoft.Testing.Platform`
- All test projects auto-reference `AI.TestingFramework` (shared ASP.NET fakes, WebSocket mocks, protocol fakes)
- Snapshot testing with Verify is available (`dotnet verify accept -y` to accept changes)
- Do not use `Directory.SetCurrentDirectory` in tests — it causes concurrency issues

## C# Conventions

- .NET 10 with C# preview features (`LangVersion: preview`), `Nullable` and `ImplicitUsings` enabled globally
- `TreatWarningsAsErrors` is enabled — if temporarily needed during refactoring, add `/p:TreatWarningsAsErrors=false` but fix all warnings before finishing
- NuGet versions are managed centrally in `Directory.Packages.props` (root for src, `test/Directory.Packages.props` for test-only). Do not add `Version` attributes in individual `.csproj` files.
- Private fields: `_camelCase`; constants: `PascalCase`; file-scoped namespaces; `var` preferred everywhere
- Formatting is defined in `.editorconfig` — Allman-style braces, 4-space indent for C#, 2-space for XML/JSON
- Never change `global.json` unless explicitly asked
