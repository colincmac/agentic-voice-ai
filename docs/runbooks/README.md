# Runbooks

Operator procedures for common day-2 tasks and failures.

| Runbook | Covers |
| --- | --- |
| [`event-grid-incomingcall-subscription.md`](event-grid-incomingcall-subscription.md) | Event Grid subscription validation handshake for the `IncomingCall` webhook — synchronous vs asynchronous response modes, the 5-minute window for the async URL, the ~24h validation retry behaviour, and the four things every handler must implement (validation envelope detection, sync echo, no-bearer-token endpoint protection, validationCode logging). |
| [`timing-and-retries.md`](timing-and-retries.md) | The full ACS Call Automation / Event Grid timing model: delivery retry backoff (10s → 12h over a 24h TTL), the ~60s `incomingCallContext` answer window, `Recognize` timeout structure, application-level retry / escalation policy, transfer / `AddParticipant` timing, idempotency on at-least-once delivery, suggested defaults for every tunable, and the production hardening checklist (dead-letter queue, dedup cache, circuit breaker, per-leg correlation, synthetic probers). |

*Still to come: the operator playbook for [ADR-0008](../adr/0008-graceful-degradation-realtime-to-dtmf.md)'s degradation tiers — how to manually pin or unpin the tier ceiling, how to read the relevant dashboards, and how to drain a region.*
