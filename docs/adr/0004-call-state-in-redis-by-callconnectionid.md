# ADR-0004 — Call/menu state in Redis keyed by `callConnectionId`; pods stateless; webhook idempotency via `(callConnectionId, sequenceNumber)`

- **Status:** Accepted
- **Date:** initial deployment

## Context

ACS Call Automation callbacks (mid-call CloudEvents) and Event Grid `IncomingCall` events are delivered to internet-reachable HTTPS endpoints with **at-least-once** semantics — retries with exponential backoff and a default 24-hour TTL ([`call-flow.md` Appendix D](../architecture/call-flow.md)). The agent app runs as multiple stateless replicas behind a load balancer (per [ADR-0002](0002-acs-call-automation-as-control-plane.md), the app is just an HTTP service); any pod can receive any callback for any active call.

For each in-flight call the app needs to remember:

- The current menu/dialog node (so a `RecognizeCompleted` knows which transition to apply).
- Per-node retry counts (for re-prompting policy — see [`call-flow.md` Appendix D §4](../architecture/call-flow.md)).
- Collected slot values (intent, account number, language, etc.) to pass downstream on `TransferCallToParticipant` as `customCallingContext`.
- The active degradation **tier** (per [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md)) so a mid-call fallback can switch dialog modes without dropping the caller.
- Correlation identifiers (`correlationId`, `serverCallId`, `callConnectionId`) for telemetry.

In-process state is unsafe because the next callback may land on a different pod; sticky sessions are not guaranteed (Event Grid and Call Automation deliver to whichever replica the load balancer picks). Equally, callbacks may be **duplicated** by retries — applying a `RecognizeCompleted` twice would advance the menu past where the caller actually is.

## Decision

- **State store:** use **Redis** (Azure Cache for Redis or compatible) as the per-call state store, keyed by **`callConnectionId`**. The repo's existing Aspire wiring already provisions Redis (see `Showcase.AppHost`), so this fits the platform.
- **Schema:** one hash (or JSON document) per `callConnectionId` containing `{menuNode, retryCount[node], slots, tier, correlationId, serverCallId, lastSequenceNumber, …}`. TTL ≥ longest plausible call duration plus a generous tail (e.g., 4 hours) so post-call telemetry/CDR work can still read it; explicit purge on `CallDisconnected`.
- **`operationContext`** is set on **every** Play / Recognize / Transfer / Add/RemoveParticipant call and carries the menu node ID (and any other dispatch context the app needs). Callbacks dispatch on `operationContext` rather than inferring intent from event type alone. This is mandatory, not optional.
- **Idempotency:** before applying any state transition, dedup on the tuple **`(callConnectionId, sequenceNumber)`** using a Redis `SET … NX EX <ttl>` (or equivalent). Duplicate deliveries become no-ops that still return `200 OK` to the publisher.
- **Pods are stateless.** No in-memory dialog state, no local file cache, no sticky-session requirement. Any replica can serve any callback.

## Consequences

- Horizontal scaling is straightforward: add replicas, no rebalancing required. Rolling deployments do not interrupt in-flight calls because the state lives in Redis.
- Redis is on the **hot path** for every callback. Its availability and p99 latency directly bound call-handling latency, so it must be sized for the full callback rate (multiply concurrent calls by typical events per call) and protected with Polly-style retries / a circuit breaker. The Aspire reference deployment uses Azure Cache for Redis with availability appropriate to the SLO.
- Webhook handlers are **idempotent by construction**: if the dedup check finds the `(callConnectionId, sequenceNumber)` already processed, the handler short-circuits. This is the correctness contract that makes ADR-0003's at-least-once delivery safe.
- Mid-call degradation (ADR-0008) reads/writes the `tier` field in the same state record. A failed realtime turn flips the tier and the next callback continues in the new mode using the same `menuNode` — no caller-visible drop.
- Custom calling context for escalations (intent, digits, language) is sourced from the same `slots` field, so the screen-pop payload sent to Dynamics CCaaS on `TransferCallToParticipant` is exactly what the IVR collected.
- Telemetry must include `correlationId`, `serverCallId`, `callConnectionId`, `operationContext`, and `sequenceNumber` on every line (already noted in [`call-flow.md` "Things worth being deliberate about"](../architecture/call-flow.md)).

## Alternatives considered

- **In-memory state with sticky sessions.** Rejected. Event Grid and Call Automation do not honour load-balancer affinity; even if they did, a pod restart during a call would lose all state for active calls on that pod.
- **Cosmos DB as the state store.** Rejected for the per-call state. Cosmos works, but Redis offers an order-of-magnitude lower per-operation latency at this access pattern (small documents, very high read/write rate, short lifetime). Cosmos remains the right store for *durable* call records (CDRs, transcripts, biometrics enrolments) — those are written **once**, after the call ends, and are out of scope for this ADR.
- **Per-pod sharding by `callConnectionId` hash.** Rejected. Adds a routing layer and reintroduces the rolling-deployment hazard. The "any pod can serve any callback" model is simpler and matches how ACS actually delivers events.
- **Skipping dedup and relying on idempotent operations only.** Rejected. State transitions like "advance to next menu node" or "increment retryCount" are not naturally idempotent; the explicit `(callConnectionId, sequenceNumber)` dedup is the simplest correct solution.
- **Using `correlationId` instead of `callConnectionId` as the key.** Rejected. `callConnectionId` is the identifier ACS Call Automation uses on every mid-call event; `correlationId` is best for cross-system support traces. They serve different purposes — both are stored, only `callConnectionId` is the primary key.
