# Conversation strategies

Each call processed by the contact-center container is driven by exactly one [`IConversationStrategy`](../../src/AgentFramework/Agents.AI.ContactCenter/Calling/IConversationStrategy.cs) instance — the "brain" of the call. The strategy consumes caller input (audio frames, DTMF tones), emits outbound directives (audio, verbs, transfers), and publishes structured `StrategyEvent`s for observers and dashboards.

This folder documents the strategies that ship in `Agents.AI.ContactCenter`, what they emit, and how they hand off information to each other during workflow step changes and degradation events.

| Document | Strategies covered |
| --- | --- |
| [`conversation-strategies.md`](conversation-strategies.md) | All strategies (Realtime voice, NLU, DTMF, Composite) and the handoff model |

## Companion ADRs

- [ADR-0008 — Graceful degradation: Realtime → DTMF](../adr/0008-graceful-degradation-realtime-to-dtmf.md)
- [`architecture/call-flow.md`](../architecture/call-flow.md) — where strategies sit relative to ACS Call Automation and the call edge
