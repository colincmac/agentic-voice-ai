# ADR-0007 — DTMF capture: ACS Recognize callback API vs ACS bi-directional media-streaming WebSocket with in-app DTMF detection

- **Status:** **Proposed** (decision pending)
- **Date:** WIP — track in the issue tracker before implementing the dependent work

## Context

DTMF (touch-tone) input is required for two distinct scenarios:

1. The classic IVR menu tree, captured in [`call-flow.md` §4](../architecture/call-flow.md), where the AI agent (or a degraded tier — see [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md)) plays a prompt and collects digits.
2. **Mid-utterance DTMF during a realtime AI session** — the caller presses `0` to reach an operator, or enters an account number while the AI is still speaking. This is the case that makes the choice non-obvious.

ACS Call Automation exposes DTMF in two ways, and they are **not equivalent in their interaction with the realtime AI audio path**:

1. **Recognize callback API** — `CallMedia.StartRecognizingAsync(CallMediaRecognizeDtmfOptions)` with `interToneTimeout`, `initialSilenceTimeout`, `maxTonesToCollect`, `stopTones`, `interruptPrompt`, and `operationContext`. ACS listens server-side, normalises DTMF (the carrier may have delivered RFC 2833 or in-band tones — ACS hands you clean digit events), and posts back `RecognizeCompleted` / `RecognizeFailed` to the mid-call callback URI. Each `StartRecognizing` call is a **discrete listening session** with its own timeout window.
2. **Bi-directional media-streaming WebSocket** — `StartMediaStreaming` connects ACS to an app-owned WebSocket that carries audio frames in both directions. ACS surfaces DTMF events on this same channel. The app sees DTMF the moment ACS detects it, with no per-event "session" to start and stop.

The realtime AI provider decision in [ADR-0006](0006-realtime-ai-voicelive-vs-gpt-realtime.md) is independent: both VoiceLive and `gpt-realtime`-direct ultimately consume audio from the bi-di WebSocket. The question here is whether DTMF should ride that **same** WebSocket or use the orthogonal Recognize callback path.

## Decision

**Pending.** The decision will be made together with [ADR-0006](0006-realtime-ai-voicelive-vs-gpt-realtime.md) because the two interact, and after a small bake-off measuring DTMF latency and reliability under each model. Prior to that decision:

- All DTMF handling **must** go through a single `IDtmfSource` abstraction in the agent code so the implementation can switch without touching dialog/menu logic.
- The reference deployment defaults to the **Recognize callback API** (`CallMediaRecognizeDtmfOptions`) for the classic IVR path. This default is provisional and explicitly subject to change by this ADR — particularly for the realtime-AI path.

### Decision drivers

| Driver | Recognize callback API | Bi-di WebSocket |
|---|---|---|
| **Mid-utterance DTMF during realtime AI** | Awkward — `StartRecognizing` competes with the realtime audio session for control of media; you have to stop/start recognise around AI turns, which loses tones during transitions. | Native — DTMF events arrive on the same channel the audio is already flowing on; the app can accept tones at any time without stopping the AI. |
| **App media handling** | None. Server-side timeouts, server-side normalisation. | App owns WebSocket lifecycle, frame parsing, dedup, and reconnection. |
| **Latency** | Add one round-trip per `StartRecognizing` call plus the configured `initialSilenceTimeout` window. | Tones are pushed as they happen; lowest possible latency. |
| **Operational simplicity** | High. One SDK call per recognise; no media plumbing. | Lower. WebSocket needs supervision (reconnect, backpressure, log correlation). |
| **Provider-side DTMF event differences** | Uniform `RecognizeCompleted` shape; ACS handles RFC 2833 vs in-band normalisation. | Same normalisation, but the event shape on the bi-di stream is different from the Recognize callback shape — needs adapting. |
| **Required for graceful degradation** | Tier 3 (DTMF-only IVR) in [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md) needs a working DTMF path that does **not** depend on the bi-di WebSocket — because Tier 3 is what we degrade to when realtime AI / media streaming is degraded. | Cannot be the only DTMF path: a media-streaming outage would also kill DTMF. |
| **Server-side timeouts** | First-class (`initialSilenceTimeout`, `interToneTimeout`). | App must implement equivalents. |

### Likely shape of the decision

The most probable outcome — to be validated by the bake-off — is a **hybrid**:

- **Tier 1 / Tier 2 (realtime or ASR-driven dialog):** consume DTMF from the **bi-di WebSocket** so digits can be accepted mid-utterance without fighting the audio session. The agent treats a digit as an interruption signal (e.g., `0` → escalate, account-number digits → fill a slot, `*` → repeat).
- **Tier 3 (DTMF-only IVR) and any path where bi-di media streaming is unavailable:** use the **Recognize callback API** (`StartRecognizing(Dtmf)`) — server-side timeouts, no app-owned media, and works even when the WebSocket is the failure mode.

If the hybrid is adopted, the `IDtmfSource` abstraction has two implementations and the active tier (per [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md)) selects which one is wired in for the call.

## Consequences (provisional)

- A single `IDtmfSource` abstraction is mandatory regardless of outcome. Dialog logic depends only on "a digit arrived for this call" — not on which API delivered it.
- If a hybrid is adopted, every dialog node needs to declare whether it expects digits during AI speech (bi-di) or only between prompts (Recognize). This is a per-node config, not a global mode.
- Telemetry includes a `dtmfSource = recognize|webSocket` dimension on every digit event so dashboards can compare reliability and latency between the two paths during and after the bake-off.
- The Recognize callback API stays in the codebase **even if** the bi-di path becomes the default for Tier 1, because Tier 3 of [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md) requires it.
- `operationContext` discipline (every Recognize tags the menu node it's listening for — see [ADR-0004](0004-call-state-in-redis-by-callconnectionid.md)) carries forward unchanged.

## Alternatives considered

- **Bi-di WebSocket only.** Rejected. A single point of failure for both audio and DTMF; if media streaming degrades, the caller loses the ability to even DTMF-out to an operator. Violates the resilience goal of [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md).
- **Recognize callback only.** Workable for classic IVR but degrades the realtime AI experience: every `StartRecognizing` either pauses the AI or loses tones overlapping the AI's audio. Acceptable as the *Tier 3* path only.
- **In-band DTMF detection from raw RTP.** Out of scope — ACS already normalises DTMF; reimplementing detection in the app gains nothing and adds DSP code.
- **A second ACS call leg dedicated to DTMF.** Rejected — adds a second call object per caller, doubles event volume, and ACS does not expose a clean way to attach a "DTMF-only listener" leg.
