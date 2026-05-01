# ADR-0001 — PSTN ingress via Teams Phone Extensibility (Resource Account → ACS)

- **Status:** Accepted
- **Date:** initial deployment

## Context

The platform needs an inbound PSTN entry point for callers to reach the AI voice agent. There are three realistic ways to land a PSTN call inside an Azure Communication Services (ACS) Call Automation app:

1. **Native ACS DID** — purchase a phone number directly on the ACS resource. Calls land straight in ACS. Requires the ACS resource to be eligible for direct PSTN number purchase in the target country/region; not always available, and unrelated to any Teams investment.
2. **Direct Routing via SBC** — terminate PSTN on a customer-owned SBC (or partner SBC) and bridge to ACS. Maximum control, but you own (or rent) and operate SBC infrastructure, certificates, SIP trunks, and codec interop.
3. **Teams Phone Extensibility (TPE)** — assign the DID to a Teams **Resource Account** (RA) backed by Microsoft Calling Plans, Operator Connect, or Direct Routing into Teams; bind that RA to the ACS resource so Teams hands the call off to ACS over the Microsoft backbone. Detailed in [`call-flow.md` §1–2](../architecture/call-flow.md), [`teams-extensibility.md`](../teams-extensibility.md), [`tpe-onboarding-guide.md`](../tpe-onboarding-guide.md), and [`tpe-brownfield.md`](../tpe-brownfield.md).

The target customers already operate Teams Phone at scale and want their existing DIDs, number-management workflows, compliance recording integrations, and Teams-side reporting to remain authoritative. They also expect to escalate calls to Dynamics 365 Contact Center (itself ACS-based), making an ACS-native call object on the inside attractive.

## Decision

- **Use Teams Phone Extensibility (TPE)** as the standard PSTN ingress: the DID lives on a Teams Resource Account, the RA is bound to the ACS resource via the ACS Telephony 1st-party application instance, and Teams delegates the call to the ACS calling backend.
- Do **not** require an SBC or a native ACS DID for the standard deployment. Both remain valid for niche scenarios (e.g., regions where TPE is unavailable, or where the customer has zero Teams footprint), but they are out of scope for the reference architecture.

## Consequences

- The DID, licensing (Teams Phone Resource Account license + Calling Plan / OC / DR number), and PSTN edge stay anchored on the **Teams** side. The customer's existing number-porting and compliance posture is preserved.
- Once the call reaches ACS, **ACS owns the media path and the call object**; the agent app interacts with the call through Call Automation only (HTTPS + CloudEvents — see [ADR-0002](0002-acs-call-automation-as-control-plane.md)).
- The handoff Teams → ACS is an **internal Microsoft delegation**, not a SIP REFER. The app never sees SIP and cannot packet-capture the Teams↔ACS leg. This drives the "no SIP stack" decision in ADR-0002.
- DTMF arrives at ACS already normalized (RFC 2833) regardless of how the carrier delivered it. App-side DTMF handling does not need to worry about in-band tones — see [`call-flow.md` "DTMF reliability"](../architecture/call-flow.md).
- Provisioning is multi-step (Resource Account, Entra app, license assignment, RA→ACS binding, Agent Provisioning Service sync, Bot Service registration, Event Grid subscription) and is split across the **Teams tenant** and the **Azure tenant**. This is captured in [`tpe-onboarding-guide.md`](../tpe-onboarding-guide.md) (greenfield) and [`tpe-brownfield.md`](../tpe-brownfield.md) (existing RA / ACS).
- Both the Teams Phone side and the ACS side bill for the call. The cost model must include Teams Phone licensing **and** ACS automation/media minutes.
- If the agent app does not answer/redirect/reject the `IncomingCall` within the ACS window (~60 s), the call **fails on the ACS side** — Teams does not silently fall back to an Auto Attendant. This shapes the answer-window SLA in [ADR-0003](0003-incomingcall-delivery-via-event-grid.md).

## Alternatives considered

- **Native ACS DID (no Teams).** Rejected as the standard path — the target customers have existing Teams Phone deployments and want number ownership to stay there. Kept as an option for greenfield deployments in regions where TPE is supported and the customer has no Teams footprint.
- **Direct Routing via SBC into ACS.** Rejected as the standard path — adds an SBC fleet, SIP/SRTP operations, and codec interop work that TPE eliminates. Reserved for customers with hard requirements on SBC-mediated recording, carrier choice, or regions without TPE.
- **Auto Attendant / Call Queue handing off to a bot.** Rejected — adds an extra prompt layer the AI agent should own itself, and the bot framework call-handling surface is narrower than ACS Call Automation (no built-in `Recognize`/`Play` verbs against the same identity).
