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

Long-form, narrative documentation that ADRs reference but do not replace. Grouped by what you're trying to do — *understand* the platform, *provision* TPE for a tenant, or *operate* it day-2.

### Understand — what the platform looks like

| Document                                                                                       | Purpose                                                                                              |
| ---------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------- |
| [`architecture/call-flow.md`](architecture/call-flow.md)                                       | End-to-end narrative call flow: PSTN → Teams RA → ACS Call Automation → Dynamics CCaaS. The canonical document referenced by ADR-0001 through ADR-0005. |
| [`architecture/sequence-diagrams.md`](architecture/sequence-diagrams.md)                       | Full PSTN → Teams → ACS → IVR → CCaaS sequence diagram in mermaid (formerly Appendix A of `call-flow.md`). |
| [`architecture/transfer-patterns.md`](architecture/transfer-patterns.md)                       | Blind vs consultative transfer patterns and the VoIP-vs-SIP transport rules for `customCallingContext` headers. |
| [`voice-identity-verification.md`](voice-identity-verification.md)                             | Operator/developer guide for the voice biometrics feature (stub mode and gRPC mode).                 |
| [`design/agentic-evals.md`](design/agentic-evals.md)                                           | Evaluation strategy for agent trajectories (judge models, metrics, Azure-format results).            |

### Provision — stand TPE up for a tenant

| Document                                                                | Purpose                                                                                              |
| ----------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------- |
| [`teams-extensibility.md`](teams-extensibility.md)                      | One-paragraph definition of Teams Phone Extensibility plus pointers to the MS Learn quickstart and the in-repo onboarding/brownfield guides. |
| [`tpe-onboarding-guide.md`](tpe-onboarding-guide.md)                    | Greenfield enterprise onboarding for TPE (resource accounts, Entra app, ACS binding, Bot Service).   |
| [`tpe-brownfield.md`](tpe-brownfield.md)                                | Connecting an existing Teams Phone resource account to an existing ACS resource.                     |

### Operate — run it in production

| Document                                                                                                              | Purpose                                                                                              |
| --------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------- |
| [`runbooks/event-grid-incomingcall-subscription.md`](runbooks/event-grid-incomingcall-subscription.md)                | Event Grid subscription validation handshake for the `IncomingCall` webhook — sync vs async, failure modes, what your handler must do. |
| [`runbooks/timing-and-retries.md`](runbooks/timing-and-retries.md)                                                    | Concrete ACS Call Automation / Event Grid timeouts, retry backoff schedules, suggested IVR defaults, and the production hardening checklist. |
| [`runbooks/README.md`](runbooks/README.md)                                                                            | Runbook index and conventions.                                                                       |

## Runbooks (`runbooks/`)

Operator procedures for common day-2 tasks and failures. See [`runbooks/README.md`](runbooks/README.md) for the index. Current entries:

- [`runbooks/event-grid-incomingcall-subscription.md`](runbooks/event-grid-incomingcall-subscription.md) — Event Grid subscription validation handshake, sync vs async response modes, retry behaviour, and what the `IncomingCall` webhook handler must do.
- [`runbooks/timing-and-retries.md`](runbooks/timing-and-retries.md) — ACS Call Automation / Event Grid timing model: delivery retries, recognise timeouts, transfer windows, suggested defaults, and the production hardening checklist (dead-letter queue, dedup cache, circuit breaker, per-leg correlation, synthetic probers).

*Still to come: the operator playbook for ADR-0008's degradation tiers (how to manually pin or unpin the tier ceiling, which dashboards to read, how to drain a region).*
