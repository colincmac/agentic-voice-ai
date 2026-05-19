# IVR Workflow YAML Schema

This document describes the declarative YAML format for authoring IVR workflows on top of
`Agents.AI.ContactCenter` and the Microsoft Agent Framework Workflows runtime.

A workflow is a single YAML document made up of:

- a **root** (`name`, `version`, `description`, `strategy`, `base`, `capabilities`, `stages`),
- a list of **stages** — each stage drives a step of the IVR call,
- optional **capabilities** — reusable business actions that stages reference by id,
- per-stage or root **intents** — caller utterances/digits that route the call.

The JSON Schema in [`ivr-workflow.schema.json`](./ivr-workflow.schema.json) is authoritative;
the prose below is a friendly summary.

## Top-level

```yaml
name: sample-banking-ivr
version: 1
description: A sample bilingual banking IVR with realtime and DTMF fallback.
strategy:
  primary: realtime
  fallback: [ nlu, dtmf ]
  prewarmTiers: [ dtmf ]
  allowMidCallDegradation: true
base:
  prompt: { ... }
  commonTools: [ transfer-to-human, end-session ]
  requiredAuthLevel: phoneRecognized
capabilities:
  - id: balance.lookup
    description: Read the caller's current balance
    requires:
      - type: auth
        level: accountVerified
    tools: [ balance-lookup-tool ]
stages:
  - id: greeting
    goal: Greet the caller and capture primary intent
    realtime:
      instructions:
        - "Welcome the caller by name when available."
      examples:
        - "Hi, thanks for calling Contoso Bank. How can I help today?"
    dtmf:
      ssmlPrompt: "Press 1 for balance, press 2 for transfers, or press 0 for an agent."
      options:
        - { digit: '1', label: Balance, intent: balance, nextStage: verify-account }
        - { digit: '2', label: Transfers, intent: transfer-funds, nextStage: verify-account }
        - { digit: '0', label: Agent, capability: transfer.to.human }
    intents:
      - { name: balance, examples: [ "balance", "account balance" ], nextStage: verify-account }
      - { name: transfer-funds, examples: [ "transfer", "send money" ], nextStage: verify-account }
```

## Strategy

`strategy.primary` declares the preferred interaction tier. `strategy.fallback` lists
ordered alternative tiers if the primary cannot be allocated or fails mid-call.
`strategy.prewarmTiers` requests pre-warming so cut-over is fast. Supported tier values
mirror the `AgentTier` enum: `realtime`, `chatCompletion`, `smallLanguageModel`, `nlu`,
`dtmf`, plus the meta value `mixed` for `primary`.

## Base

`base.prompt` mirrors the `RealtimePrompt` model:

- `role.identity`, `role.objective`, `role.characterTraits`
- `personality.personality`, `personality.tone`, plus optional `length`, `pacing`,
  `enthusiasm`, `formality`, `emotion`, `fillerWords`, `enforceVariety`
- `context` — free-form context appended to the rendered prompt
- `pronunciations` — list of `{ word, pronunciation }` (or `ipa`)
- `safety` — escalation triggers and phrasing

`base.commonTools` lists tool names available on every stage; tools are resolved through
the DI-registered `IIvrToolRegistry`.

## Capabilities

Capabilities are reusable business actions. Each capability declares its id, optional
description, preconditions (`requires`), tools, and tool usage rules. Stages reference
capabilities by id.

## Stages

A stage is the unit of interaction. It may declare:

- `requires` — entry guards (auth level, required state, previous stage, predicate)
- `strategy` — overrides the root strategy for this stage only
- `realtime` — instructions/examples/tools/toolRules surfaced to the realtime agent
- `dtmf` — digit menu, audio prompts, and optional digit-collection block
- `intents` — locally-scoped intents (utterance + routing)
- `capabilities` — capability ids exposed at this stage
- `exitWhen`, `onExit`, `transitions` — declarative routing
- `collects`, `requiredState` — state contract for the stage
- `maxRetries`, `maxDuration` — operational limits
- `terminal: true` — completes the workflow upon entry/exit

### DTMF Options

Each `dtmf.options[]` entry binds a single digit (`0`-`9`, `*`, `#`) to a routing
decision: an intent, a capability, a direct stage transition, or a tool invocation.
The compiler chooses the strongest signal in that order — an explicit `nextStage` wins
ties with `intent`/`capability`.

### Digit Collection

`dtmf.collect` activates buffered digit collection (e.g., account number). It declares
min/max digits, terminator, an inter-digit timeout, and a `validator` tool name resolved
through the registry. On success the buffer is stored under `collectedStateKey` and the
workflow transitions to `onValidNextStage`.

### Intents

Intent declarations look the same at root and stage level. Stage intents extend (not
shadow) root intents. Each intent maps to `nextStage`, `capability`, or both.

## Guards

`requires:` accepts an array of guard objects. Built-in `type` values:

- `auth` — `level` is one of `none`, `phoneRecognized`, `accountVerified`,
  `securityQuestionPassed`, `fullyAuthenticated`.
- `state` — `key` or `keys` must be present in workflow state.
- `previousStage` — `stage` must already be in `completedSteps`.
- `predicate` — `predicate` names a delegate registered with `IIvrGuardFactory`.

Custom guard kinds are dispatched to registered `IIvrGuardFactory` implementations.

## Tools

Tool names in `commonTools`, `tools`, `dtmf.options[].tool`, and `dtmf.collect.validator`
are resolved at compile time against the DI-registered `IIvrToolRegistry`. Production
hosts typically register tools through `[McpServerTool]` discovery or by calling
`AddIvrTool(...)` during DI setup.
