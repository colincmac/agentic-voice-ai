# Documentation

Architecture and design documentation for the Agents v2 Accelerator — a real-time voice agent platform built on Azure Communication Services (ACS) Call Automation, Teams Phone Extensibility (TPE), and pluggable realtime AI providers.

## Architecture Decision Records (`adr/`)

Each ADR records a significant choice, the alternatives considered, and the consequences. Items marked **Proposed** are decisions that remain open — see the issue tracker before implementing the dependent work.

| #    | Title                                                                                                | Status                                |
| ---- | ---------------------------------------------------------------------------------------------------- | ------------------------------------- |
| [0001](adr/0001-pstn-ingress-via-tpe.md) | PSTN ingress via Teams Phone Extensibility (RA → ACS)                                    | Accepted                              |
| [0002](adr/0002-acs-call-automation-as-control-plane.md) | ACS Call Automation as the call control plane (HTTPS + CloudEvents, no SIP stack in app) | Accepted                              |
| [0003](adr/0003-incomingcall-delivery-via-event-grid.md) | `IncomingCall` delivery via Event Grid webhook with synchronous validation handshake     | Accepted                              |
| [0004](adr/0004-call-state-in-redis-by-callconnectionid.md) | Call/menu state in Redis keyed by `callConnectionId`; pods stateless; webhook idempotency | Accepted                              |
| [0005](adr/0005-escalation-blind-vs-consultative-transfer.md) | Escalation: blind `TransferCallToParticipant` is default; consultative reserved for VIP/supervisor | Accepted                              |
| [0006](adr/0006-realtime-ai-voicelive-vs-gpt-realtime.md) | Realtime AI provider: Azure VoiceLive vs OpenAI `gpt-realtime` (direct)                  | **Proposed** (decision pending)       |
| [0007](adr/0007-dtmf-bidirectional-websocket-vs-callback-api.md) | DTMF capture: ACS Recognize callback API vs ACS bi-directional media-streaming WebSocket | **Proposed** (decision pending)       |
| [0008](adr/0008-graceful-degradation-realtime-to-dtmf.md) | Graceful degradation from realtime AI down to DTMF-only (high-volume / provider-degraded) | **Proposed** (decision pending)       |
| [0009](adr/0009-voice-biometrics-stub-vs-grpc.md) | Voice biometrics: pluggable `IVoiceBiometricEvaluator` with stub default and gRPC adapter | Accepted                              |

## Architecture & guides

Long-form, narrative documentation that ADRs reference but do not replace.

| Document                                                                | Purpose                                                                                              |
| ----------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------- |
| [`architecture/call-flow.md`](architecture/call-flow.md)                | End-to-end call flow: PSTN → Teams RA → ACS Call Automation → Dynamics CCaaS, with sequence diagrams, timeouts, and the Event Grid validation handshake. The canonical narrative referenced by ADR-0001 through ADR-0005. |
| [`design/agentic-evals.md`](design/agentic-evals.md)                    | Evaluation strategy for agent trajectories (judge models, metrics, Azure-format results).            |
| [`teams-extensibility.md`](teams-extensibility.md)                      | Microsoft Learn–style overview of Teams Phone Extensibility (TPE) and how ACS plugs in.              |
| [`tpe-onboarding-guide.md`](tpe-onboarding-guide.md)                    | Greenfield enterprise onboarding for TPE (resource accounts, Entra app, ACS binding, Bot Service).   |
| [`tpe-brownfield.md`](tpe-brownfield.md)                                | Connecting an existing Teams Phone resource account to an existing ACS resource.                     |
| [`voice-identity-verification.md`](voice-identity-verification.md)      | Operator/developer guide for the voice biometrics feature (stub mode and gRPC mode).                 |

## Runbooks (`runbooks/`)

Operator procedures for common day-2 tasks and failures. *(Placeholder — runbooks will be added as scenarios stabilise; see ADR-0008 for the degradation playbook this directory will eventually formalise.)*
