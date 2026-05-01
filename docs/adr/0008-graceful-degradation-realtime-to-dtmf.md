# ADR-0008 — Graceful degradation from realtime AI down to DTMF-only

- **Status:** **Proposed** (decision pending)
- **Date:** WIP — track in the issue tracker before implementing the dependent work

## Context

Realtime AI (per [ADR-0006](0006-realtime-ai-voicelive-vs-gpt-realtime.md)) is the headline caller experience but it is also the most fragile and the most rate-limited link in the call path:

- Concurrent realtime sessions are quota-bounded per region and per subscription.
- Model providers can return `429 Throttling` or have regional outages.
- First-token latency under load can blow past the budget that keeps a voice conversation natural (>~800 ms feels broken).
- The bi-di media-streaming WebSocket between ACS and the app (or the model) can disconnect mid-call.
- A high-volume marketing event or an upstream incident can spike call volume past Tier-1 capacity.

Hard-failing the caller (silence, dropped call, generic error) in any of these scenarios is unacceptable. The system must **degrade gracefully** to progressively simpler — and progressively more reliable — call-handling modes, ideally without the caller noticing the transition. The classic DTMF IVR tree from [`call-flow.md` §4](../architecture/call-flow.md) is the well-understood floor: it works as long as ACS Call Automation works.

## Decision

**Pending in detail; tier model proposed.** The intent is to ship four explicit degradation tiers, with explicit triggers, an explicit selection mechanism, and explicit telemetry. The exact thresholds and the home of the tier-selection logic (in-app vs Front Door / config service) will be finalised after a load test and after [ADR-0006](0006-realtime-ai-voicelive-vs-gpt-realtime.md) and [ADR-0007](0007-dtmf-bidirectional-websocket-vs-callback-api.md) land.

### Tier model

| Tier | Caller experience | Dependencies |
|---|---|---|
| **Tier 1 — Full realtime AI** | Free-form natural conversation, semantic intent, mid-utterance barge-in, async function calling. | Realtime AI provider (per [ADR-0006](0006-realtime-ai-voicelive-vs-gpt-realtime.md)) + ACS bi-di media streaming + tools. |
| **Tier 2 — TTS prompts + ASR for keyword/intent** | Sounds like a smart IVR: AI-generated prompts, but the app uses turn-based ASR (Azure Speech via the Cognitive Services link on the ACS resource) instead of a realtime model. Slower turns, no true barge-in by speech, but still natural-language. | ACS `Play(TextSource)` + ACS `Recognize(Speech)` + Cognitive Services. **No** realtime model; **no** bi-di media streaming required. |
| **Tier 3 — TTS prompts + DTMF-only IVR** | The classic touch-tone tree from [`call-flow.md` §4](../architecture/call-flow.md). | ACS `Play` + ACS `Recognize(Dtmf)` callback API ([ADR-0007](0007-dtmf-bidirectional-websocket-vs-callback-api.md)) + Cognitive Services for TTS. |
| **Tier 4 — Pre-recorded prompt + immediate transfer or "call back later"** | A short pre-recorded apology, then either `TransferCallToParticipant` to the CCaaS queue's after-hours / overflow line, or a polite "please try again later" + `HangUp`. No menu, no AI. | ACS `Play(FileSource)` + `TransferCallToParticipant` or `HangUp`. Static asset only — no Cognitive Services dependency. |

### Triggers (degradation downward)

A circuit breaker / health signal evaluates these continuously and updates the **active tier ceiling** for new calls; in-call degradation happens reactively on hard error.

| Trigger | Action |
|---|---|
| Realtime provider 429 / quota exceeded | Cap new calls at Tier 2; existing Tier-1 calls continue. |
| Realtime p95 first-token latency over budget for N minutes | Cap new calls at Tier 2; emit alert. |
| Realtime provider 5xx / regional outage signal | Cap new calls at Tier 2; existing Tier-1 calls fall back on next session error. |
| ACS bi-di media streaming session failures over threshold | Cap new calls at Tier 3 (no bi-di required from Tier 3 down). |
| Cognitive Services unavailable | Cap new calls at Tier 4 (no TTS/ASR needed). |
| Explicit ops "load shed" flag | Cap new calls at the operator-chosen tier; existing calls keep their tier. |
| Concurrent active calls > soft cap | Cap new calls at Tier 3; alert. |
| Concurrent active calls > hard cap | Cap new calls at Tier 4; alert. |

### Recovery (degradation upward)

- Tier ceiling can be raised back automatically when the triggering signal has been clear for a configurable cool-down (e.g., 5 minutes).
- Existing calls do **not** upgrade mid-call; they finish in the tier they started (or the tier they degraded into).

### Mechanism

- **Tier selection at `IncomingCall` time** is the primary path: the Event Grid handler ([ADR-0003](0003-incomingcall-delivery-via-event-grid.md)) reads the current tier ceiling from a fast, in-process cache backed by the circuit breaker, stamps `tier` into the per-call Redis state ([ADR-0004](0004-call-state-in-redis-by-callconnectionid.md)), and configures the dialog accordingly *before* `AnswerCall`.
- **Mid-call degradation** is reactive: a hard failure in the realtime session (provider error, WebSocket disconnect that doesn't recover) catches into a downgrade routine that:
  1. Flips `tier` in the Redis state record.
  2. Plays a short bridging prompt ("Let me put that another way…" or similar — TTS, never silence).
  3. Restarts the dialog at the **same logical menu node** in the lower tier (e.g., a Tier-1 "what can I help with?" turn becomes a Tier-3 "press 1 for billing, 2 for support, 3 for…" prompt, both keyed by the same `menuNode = "root"`).
  - The caller is never disconnected. The transition is audible only as a brief reformulation.
- **Tier 4 is always available.** Tier 4 prompts and the overflow transfer target are static assets cached at deploy time and do not require any dependency that could itself be degraded.

### Telemetry

- `tier` is a first-class dimension on every metric and log line for the call.
- `tierTransitions` counter per call (0 for the happy path).
- `degradationReason` tag on each transition (`provider_429`, `latency_budget`, `media_stream_failure`, `ops_load_shed`, `concurrent_calls_softcap`, …).
- A dashboard tile shows current tier ceiling and rolling distribution of calls per tier.

### Open questions (to resolve before status changes to Accepted)

1. **Where the tier ceiling lives** — in-process per-pod (eventually consistent across replicas, with Redis as the source of truth) vs an external config service / Front Door routing rule. Per-pod is simpler and avoids a new dependency on the answer path; external is more globally consistent.
2. **Whether to pre-warm Tier 3 menus for every call** so the mid-call fallback has zero cold-start cost. Cheap if the menu definitions are static; revisit if they become dynamic per-tenant.
3. **How aggressive the automatic upgrade should be.** Conservative (operator-confirmed) is safer; aggressive (auto-upgrade after cool-down) is more responsive. Suggest: aggressive with explicit operator override.
4. **Whether Tier 2 is worth shipping at all.** It sits between two well-understood tiers and may not justify the dialog-design effort if Tier 1 ↔ Tier 3 jumps are good enough. Decide after the [ADR-0006](0006-realtime-ai-voicelive-vs-gpt-realtime.md) bake-off.

## Consequences (provisional)

- The dialog model **must** be authored in a way that every menu node has a representation in every tier the call could enter from that point. Pragmatically this means the dialog graph is the canonical shape and each tier provides a **renderer** (realtime prompt template, ASR grammar, DTMF prompt). The state in Redis ([ADR-0004](0004-call-state-in-redis-by-callconnectionid.md)) holds tier-independent slot values.
- [ADR-0007](0007-dtmf-bidirectional-websocket-vs-callback-api.md)'s Recognize callback path is a **hard requirement** for Tier 3, regardless of which DTMF source Tier 1 uses. The Recognize implementation cannot be dropped.
- [ADR-0005](0005-escalation-blind-vs-consultative-transfer.md)'s blind-transfer escalation works in every tier; only Tier 4 forces an immediate transfer instead of an opt-in one.
- A regional realtime AI outage no longer takes the platform down — it caps the tier ceiling and the call experience changes shape but still resolves intents.
- Cost characteristics shift with tier. Reporting needs cost-per-call broken down by tier so a sustained drop to Tier 2/3 surfaces as both a quality signal and a cost signal.
- The synthetic call probers from [`call-flow.md` "Production hardening checklist"](../architecture/call-flow.md) need at least one synthetic per tier so a regression in (say) Tier 3 is caught even when Tier 1 is healthy and serving most traffic.

## Alternatives considered

- **No degradation; fail to a generic "we're experiencing issues" message.** Rejected. Squanders the entire investment in PSTN ingress and CCaaS escalation; loses callers; gives no SRE knob to handle bursts.
- **Single fallback (realtime AI → DTMF only, no Tier 2/4).** Workable as a v1, but loses the partial degradation that Tier 2 / Tier 4 provide. The intent is to ship the four-tier model; a v1 with Tier 1 + Tier 3 + Tier 4 (skipping Tier 2) is an acceptable interim.
- **Per-tenant capacity reservations only (no degradation).** Rejected as the only mechanism — reservations alone do not handle provider 5xx, regional outages, or media-streaming failures. Reservations are complementary to, not a substitute for, tier-based degradation.
- **Front-Door-level routing to a "lite" service.** Rejected as the primary mechanism — the tier decision needs per-call context (operationContext, dialog node, customer tenancy) that Front Door doesn't see. Front Door can do coarse load-shedding (e.g., reject N% of new calls during a declared incident) as a complement.
- **Always run Tier 3 in parallel as a hot standby.** Rejected — doubles cost and adds an arbitration problem. The pre-warm of Tier 3 *menus* (open question 2 above) is a cheaper version of the same idea without running a parallel dialog.
