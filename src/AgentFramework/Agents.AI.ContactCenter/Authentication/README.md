# Caller Authentication

Single, AspNetCore-style authentication framework for the **Agents.AI.ContactCenter** SDK.
Authenticate inbound callers passively (ANI lookup), elevate them mid-call through pluggable
challenges (PIN, SMS OTP, voice biometric, Entra Verified ID, …), and gate AI tools / IVR
steps on the resulting verification level.

## Core concepts

| Concept | Type | Notes |
|---|---|---|
| Verification level | [`CallerVerificationLevel`](./CallerVerificationLevel.cs) | Single ordered enum: `None` → `AniMatch` → `KnowledgeBased` → `MultiFactor` → `VoiceBiometric` → `EntraVerifiedId` → `Strong`. |
| Established caller | [`CallerIdentity`](./CallerIdentity.cs) | Immutable record. `CallerIdentity.Anonymous` is the call-start default. |
| Per-call state | [`CallerAuthenticationState`](./CallerAuthenticationState.cs) | Scoped, thread-safe. Holds current `Identity`, audit `Steps`, `PendingChallenge`. `TryPromote` is the **only** way to mutate identity. |
| One verification method | [`ICallerAuthenticator`](./ICallerAuthenticator.cs) | Stateless. Returns an `AuthenticationOutcome`. |
| Outcome | [`AuthenticationOutcome`](./AuthenticationOutcome.cs) | `Authenticated` \| `NotApplicable` \| `Failed` \| `NeedsChallenge`. |
| Chain runner | [`IAuthenticationOrchestrator`](./IAuthenticationOrchestrator.cs) → [`AuthenticationOrchestrator`](./AuthenticationOrchestrator.cs) | Runs authenticators in DI order, short-circuits on `Failed` / `NeedsChallenge`, records every attempt. |
| Mid-call dispatch | [`ICallerElevationDispatcher`](./ICallerElevationDispatcher.cs) → [`CallerElevationDispatcher`](./CallerElevationDispatcher.cs) | Tools call this to run a single named authenticator and emit the same `StrategyEvent`s the call-start runner produces. |
| Call-start helper | [`CallerAuthenticationRunner`](./CallerAuthenticationRunner.cs) | Strategies (`RealtimeVoiceStrategy`, `DtmfStreamingStrategy`, …) call this once on attach. |

## Built-in authenticators

| Authenticator | Source | Elevates to | Trigger |
|---|---|---|---|
| `AniIdentityLookupAuthenticator` | [`AniIdentityLookupAuthenticator.cs`](./AniIdentityLookupAuthenticator.cs) | `AniMatch` | Passive — fires on every call start, uses `ICallerDirectory`. |
| `PinAuthenticator` | [`PinAuthenticator.cs`](./PinAuthenticator.cs) | `KnowledgeBased` | Tool sets `PinAttempt.Digits` then dispatches `"Pin"`. Validation delegated to host `IPinValidator`. |
| `SmsOtpAuthenticator` | [`SmsOtpAuthenticator.cs`](./SmsOtpAuthenticator.cs) | `MultiFactor` | Two-phase: first run emits `NeedsChallenge` + sends code via `ISmsOtpSender`; second run consumes `SmsOtpAttempt.{ChallengeId,Code}` from `IChallengeStore`. |
| `AnonymousCallerAuthenticator` | [`AnonymousCallerAuthenticator.cs`](./AnonymousCallerAuthenticator.cs) | — | Always `NotApplicable`. Registered by default so the chain never enumerates an empty list. |

Voice-biometric and Entra Verified ID flows live under [`../Authorization/Biometrics/`](../Authorization/Biometrics/)
and [`../Authorization/IdentityVerification/`](../Authorization/IdentityVerification/) respectively. Wrap them in
an `ICallerAuthenticator` to plug them into the same chain.

## DI registration

```csharp
builder.AddCallSessionContainer()
    .AddCallerAuthentication()                                  // state + orchestrator + dispatcher + challenge store + handler
    .AddAniIdentityLookupAuthenticator<MyCallerDirectory>()     // passive ANI
    .AddPinAuthenticator<MyPinValidator>()                      // PIN elevation
    .AddCallerAuthenticator<SmsOtpAuthenticator>()              // OTP elevation
    .AddCallerAuthenticationTools();                            // surface validate-pin / submit-sms-otp / … as AI tools
// + host-supplied `ISmsOtpSender` registration
```

`AddCallerAuthentication()` (see [`../DependencyInjection/CallerAuthenticationContainerExtensions.cs`](../DependencyInjection/CallerAuthenticationContainerExtensions.cs)) registers:
- `CallerAuthenticationState` (scoped)
- `IAuthenticationOrchestrator` → `AuthenticationOrchestrator` (scoped)
- `ICallerElevationDispatcher` → `CallerElevationDispatcher` (scoped)
- `IChallengeStore` → `InMemoryChallengeStore` (singleton; replace for distributed deployments)
- `IToolApprovalHandler` → `CallerVerificationApprovalHandler` (singleton, enumerable)
- `AnonymousCallerAuthenticator` fallback (singleton, enumerable)

## Two-layer tool / step gating

### Per-tool — `[RequiresCallerVerification]`

Decorate any AI tool method with [`RequiresCallerVerificationAttribute`](./RequiresCallerVerification.cs). The
[`AuthorizingAgentFunction`](../../Agents.AI.Extensions/RealtimeAgentHelpers/AuthorizingRealtimeAIAgent.cs)
discovers it through the existing `IToolApprovalRequirementData` contract; the registered
`CallerVerificationApprovalHandler` reads `CallerAuthenticationState.Identity.VerificationLevel`
from the per-call DI scope and short-circuits the tool invocation when the caller is below the
required level, returning `OnFailureResponse` to the agent transcript.

```csharp
[Description("Move funds between caller's accounts.")]
[RequiresCallerVerification(
    CallerVerificationLevel.MultiFactor,
    FailureMessage = "Transfers require multi-factor verification.")]
public Task<TransferResult> TransferFunds(string fromId, string toId, decimal amount) { … }
```

Failure path returns the requirement's `OnFailureResponse` as the tool result instead of
invoking the underlying method — the caller never sees the privileged code path.

### Per-IVR-step — `MinimumVerificationGuard`

Plug [`MinimumVerificationGuard`](./MinimumVerificationGuard.cs) into any step's `Guards` to
gate entry on the live `CallerVerificationLevel`:

```csharp
new RealtimeIvrWorkflowStep("transfer-prompt", …)
{
    Guards = [ new MinimumVerificationGuard(CallerVerificationLevel.MultiFactor) ],
}
```

For YAML-driven flows the built-in `auth` guard factory accepts the same levels —
see [`../IvrWorkflow/Guards/BuiltInGuardFactories.cs`](../IvrWorkflow/Guards/BuiltInGuardFactories.cs).
Accepted aliases per level:

| Level | YAML aliases |
|---|---|
| `None` | `none` |
| `AniMatch` | `aniMatch`, `ani`, `phone` |
| `KnowledgeBased` | `knowledgeBased`, `knowledge`, `kba`, `pin` |
| `MultiFactor` | `multiFactor`, `mfa` |
| `VoiceBiometric` | `voiceBiometric`, `biometric`, `voice` |
| `EntraVerifiedId` | `entraVerifiedId`, `verifiedId` |
| `Strong` | `strong` |

```yaml
stages:
  - id: capability:card.activate
    requires:
      - type: auth
        level: multiFactor
```

## Mid-call elevation flow

Three layered options, pick whichever matches your tool surface:

### 1. Drop-in toolset — `CallerAuthenticationTools` (recommended)

Register the canonical PIN / OTP elevation tools as an `IAIToolCollection`; the realtime /
chat agent picks them up automatically from DI. Hosts don't touch
`ICallerElevationDispatcher`, `PinAttempt`, or `SmsOtpAttempt` at all.

```csharp
builder.AddCallSessionContainer()
    .AddCallerAuthentication()
    .AddAniIdentityLookupAuthenticator<MyCallerDirectory>()
    .AddPinAuthenticator<MyPinValidator>()
    .AddCallerAuthenticator<SmsOtpAuthenticator>()
    .AddCallerAuthenticationTools();   // 👈 surfaces validate-pin / request-sms-otp / submit-sms-otp
```

The collection self-gates: `validate-pin` only appears when a `PinAuthenticator` chain is
registered, OTP tools only appear when an `SmsOtpAuthenticator` chain is. Each tool
returns a [`CallerElevationToolResult`](./CallerAuthenticationTools.cs) `{ Success, Level, Message, ChallengeId?, ChallengePrompt? }`
and is gated behind `[RequiresCallerVerification(AniMatch)]` — anonymous callers can't
self-elevate.

### 2. Wrap a host tool — `ElevatingAIFunction`

If you already own a tool that collects the secret, wrap it so the SDK dispatches the
matching authenticator after the inner tool returns successfully:

```csharp
builder.AddTool("collect-and-validate-pin", sp =>
{
    var inner = AIFunctionFactory.Create(MyPinCollectorTool.Build(sp));
    return ElevatingAIFunction.Wrap(inner, authenticatorName: "Pin");
});
```

The wrapper runs the inner tool, peeks at its result (default: any non-null result whose
`bool Success` property — if present — is `true`), then dispatches the named authenticator
through `ICallerElevationDispatcher`. The inner tool is still responsible for stashing
the secret on the matching attempt object (`PinAttempt.Digits`, `SmsOtpAttempt.Code`, …)
before returning. Pass a custom `successPredicate` if your envelope shape differs.

### 3. Call the dispatcher directly

When you need full control (custom result envelope, non-standard authenticator name,
side-effects), call `ICallerElevationDispatcher.DispatchAsync` yourself:

```csharp
[Description("Validate the caller's PIN.")]
[RequiresCallerVerification(CallerVerificationLevel.AniMatch)]
AuthValidationResult ValidatePin(string digits, IServiceProvider services)
{
    var dispatcher = services.GetRequiredService<ICallerElevationDispatcher>();
    var attempt    = services.GetRequiredService<PinAttempt>();
    attempt.Digits = digits;

    var result = dispatcher.DispatchAsync("Pin", callId).GetAwaiter().GetResult();
    var step   = result.Steps.LastOrDefault(s => s.AuthenticatorName == "Pin");
    return step?.Outcome switch
    {
        AuthenticationOutcome.Authenticated => new AuthValidationResult(true, "Verified."),
        AuthenticationOutcome.Failed f       => new AuthValidationResult(false, f.Reason),
        _                                    => new AuthValidationResult(false, "PIN authenticator not registered."),
    };
}
```

Tools must never call `CallerAuthenticationState.TryPromote` directly — always go
through one of the three paths above so the state-machine + event surface stays
consistent.

The dispatcher itself:
1. Looks up the named `ICallerAuthenticator` (case-insensitive).
2. Runs it inside the per-call DI scope.
3. Records the step on `CallerAuthenticationState` and calls `TryPromote` on `Authenticated`.
4. When a `ChannelWriter<StrategyEvent>` is supplied, emits the same events the call-start
   runner produces — `CallerIdentified`, `CallerAuthenticationFailed`,
   `CallerAuthenticationChallenge`, `CallerVerificationLevelChanged` — so observers can't
   tell call-start from mid-call elevations apart.

## Strategy events

All authentication-related events flow through the strategy event channel observers already
consume (see [`../Calling/StrategyEvent.cs`](../Calling/StrategyEvent.cs)):

- `StrategyEvent.CallerIdentified(identity, authenticatorName, at)`
- `StrategyEvent.CallerAuthenticationFailed(authenticatorName, reason, at)`
- `StrategyEvent.CallerAuthenticationChallenge(challenge, at)`
- `StrategyEvent.CallerVerificationLevelChanged(from, to, at)`

## Writing a new authenticator

```csharp
public sealed class EntraVerifiedIdAuthenticator(IEntraVidClient client) : ICallerAuthenticator
{
    public string Name => "EntraVerifiedId";

    public async Task<AuthenticationOutcome> AuthenticateAsync(
        AuthenticationContext context,
        CancellationToken cancellationToken = default)
    {
        var presentation = await client.RequestPresentationAsync(context.CallId, cancellationToken);
        if (presentation is null) { return new AuthenticationOutcome.NotApplicable("VID not configured."); }
        if (!presentation.IsValid) { return new AuthenticationOutcome.Failed(presentation.Error ?? "VID rejected."); }

        var identity = context.CurrentIdentity with
        {
            VerificationLevel = CallerVerificationLevel.EntraVerifiedId,
            AuthenticatedBy   = Name,
            AuthenticatedAt   = DateTimeOffset.UtcNow,
            EntraObjectId     = presentation.Subject,
        };
        return new AuthenticationOutcome.Authenticated(identity);
    }
}

// Registration
builder.AddCallerAuthenticator<EntraVerifiedIdAuthenticator>();
```

Rules of thumb:
- **Stateless.** All per-call state lives on `CallerAuthenticationState` / `IChallengeStore`.
- **Idempotent.** The chain may run more than once per call. Return `NotApplicable` quickly
  when nothing actionable is in scope.
- **Honest about elevation.** Only set `VerificationLevel` to a level you actually proved.
- **No back-channel writes.** Always go through `Authenticated` → orchestrator/dispatcher
  → `TryPromote`. Never mutate `CallerAuthenticationState.Identity` directly.

## Testing

See [`test/Agents.AI.ContactCenter.Tests/Authentication/AuthenticationTests.cs`](../../../../test/Agents.AI.ContactCenter.Tests/Authentication/AuthenticationTests.cs)
for end-to-end coverage of the orchestrator, dispatcher, both built-in chain authenticators,
the IVR guard, and the tool-approval requirement.

## Strategy execution model

Every conversation strategy in [`../Calling/Strategies/`](../Calling/Strategies/) follows the
same authentication shape — only the *surface* the caller interacts with changes. There are
three enforcement points and three layered guards, and **all** strategies hit them in the
same order:

```
┌── Call start ────────────────┐   ┌── Step transition ───────────┐   ┌── Tool invocation ───────────┐
│ CallerAuthenticationRunner   │   │ RealtimeIvrWorkflowController│   │ IvrStepGuards (wrap or pre)  │
│   .RunAsync(...)             │ → │   .TransitionToStepAsync     │ → │ + AuthorizingAgentFunction   │
│   → orchestrator runs ANI    │   │   .EvaluateGuardsAsync       │   │   ([RequiresCallerVerif…])   │
│   → state.TryPromote         │   │   blocks on guard failure    │   │   blocks on requirement fail │
└──────────────────────────────┘   └──────────────────────────────┘   └──────────────────────────────┘
```

Mid-call elevation (PIN, OTP) is driven by tools dispatching through
`ICallerElevationDispatcher` between steps — the dispatcher promotes
`CallerAuthenticationState`, which `MinimumVerificationGuard` re-reads on the next
transition attempt, which unblocks the gated step. No strategy needs custom auth code.

### Per-strategy behavior

| Strategy | Source | Call-start auth | Step-transition guards | Tool gating |
|---|---|---|---|---|
| **Realtime voice** | [`RealtimeVoiceStrategy`](../Calling/Strategies/RealtimeVoice/RealtimeVoiceStrategy.cs) | `CallerAuthenticationRunner.RunAsync` on attach (line ~157). ANI fires once, `CallerIdentified` event surfaces. | `RealtimeIvrWorkflowController.TransitionToStepAsync` evaluates the target step's guards. `MinimumVerificationGuard` rejection keeps the model on the current step. | **Two layers stack**: `IvrWorkflowNavigator.WrapToolsWithCurrentGuards` wraps every per-step tool in a `GuardedAIFunction` (line ~180), AND `AuthorizingAgentFunction` evaluates `[RequiresCallerVerification]` through the ToolApproval pipeline. Either layer can block. |
| **NLU + DTMF fallback** | [`NluConversationStrategy`](../Calling/Strategies/Nlu/NluConversationStrategy.cs) | `CallerAuthenticationRunner.RunAsync` on attach (line ~207). | Same `RealtimeIvrWorkflowController` path. Step transitions driven by intent classifier results route through the same `EvaluateGuardsAsync`. | DTMF action tools go through `IvrWorkflowNavigator.InvokeActionAsync` (line ~624) which runs `CurrentStep.Guards` BEFORE invoking. SLM-invoked function calls go through the `AuthorizingAgentFunction` pipeline. |
| **DTMF streaming** | [`DtmfStreamingStrategy`](../Calling/Strategies/Dtmf/DtmfStreamingStrategy.cs) | `CallerAuthenticationRunner.RunAsync` on attach (line ~174). | Same controller path. | DTMF menu options resolve to tools invoked via `IvrWorkflowNavigator.InvokeActionAsync` (line ~355) — guards run inline, rejection plays the step's failure prompt and stays on the menu. |
| **DTMF verb** | [`DtmfVerbStrategy`](../Calling/Strategies/Dtmf/DtmfVerbStrategy.cs) | `CallerAuthenticationRunner.RunAsync` on attach (line ~160). | Same controller path. | Same `InvokeActionAsync` path as streaming DTMF; difference is platform-side TTS vs. local PCM. |
| **Composite fallback** | [`CompositeFallbackStrategy`](../Calling/Strategies/Composite/CompositeFallbackStrategy.cs) | The composite owns the call's DI scope. When the active inner strategy degrades, the new inner strategy reuses the SAME `CallerAuthenticationState` and `IvrWorkflowState` — the caller never re-authenticates. | Inherited from the active inner strategy. | Inherited from the active inner strategy. |

### End-to-end auth flow against an IVR Realtime Workflow

Take [`authenticated-realtime.yaml`](../../../../src/Agents/Showcase.Agent.VoiceAgent/Workflow/Samples/authenticated-realtime.yaml)
running under `RealtimeVoiceStrategy`:

1. **Inbound call attached.** `RealtimeVoiceStrategy.StartAsync` calls
   `CallerAuthenticationRunner.RunAsync`. `AniIdentityLookupAuthenticator` resolves the
   caller from `ICallerDirectory`; `CallerAuthenticationState.Identity` is promoted to
   `AniMatch`. `StrategyEvent.CallerIdentified` is emitted.
2. **Initial step (`greeting`).** No `requires:` block → no guard. Model addresses the
   caller by name (filled into `ConversationContext.CallerName` by the runner).
3. **Transition to `verify-identity`.** The step exposes `confirm-identity` as a tool.
   The model asks the caller to read back their PIN. Each tool surfaced for the step is
   wrapped by `WrapToolsWithCurrentGuards` (no guard here yet) AND by
   `AuthorizingAgentFunction` (which reads `[RequiresCallerVerification(AniMatch)]`).
   Because the caller is already `AniMatch`, the requirement passes.
4. **Caller speaks PIN → model calls `confirm-identity` (or the SDK-supplied
   `validate-pin` from `CallerAuthenticationTools`).** The tool stashes digits on
   `PinAttempt`, calls `ICallerElevationDispatcher.DispatchAsync("Pin", callId)`. The
   dispatcher runs `PinAuthenticator` → `IPinValidator.ValidateAsync` → promotes the
   identity to `KnowledgeBased`. `StrategyEvent.CallerVerificationLevelChanged(AniMatch,
   KnowledgeBased)` is emitted. Tool returns `{ Success = true, … }`.
5. **(Optional) MFA step.** If the workflow requires it, the model calls
   `request-sms-otp` → `SmsOtpAuthenticator` returns `NeedsChallenge`, OTP is sent,
   `StrategyEvent.CallerAuthenticationChallenge` is emitted. Model reads the prompt to
   the caller, collects the code, calls `submit-sms-otp(challengeId, code)` → promotes
   to `MultiFactor`.
6. **Transition to a guarded step** (e.g. `funds-transfer` with
   `requires: { type: auth, level: multiFactor }`).
   `RealtimeIvrWorkflowController.TransitionToStepAsync` runs `EvaluateGuardsAsync`.
   `MinimumVerificationGuard(MultiFactor)` reads
   `IvrWorkflowState.VerificationLevel` — promoted to `MultiFactor` in step 5 — and
   passes. Transition completes.
7. **Caller invokes a privileged tool** (e.g. `transfer-funds`
   `[RequiresCallerVerification(MultiFactor)]`). `AuthorizingAgentFunction` runs the
   ToolApproval pipeline; `CallerVerificationApprovalHandler.HandleRequirementAsync`
   reads `CallerAuthenticationState.Identity.VerificationLevel`, sees `MultiFactor`,
   calls `Succeed`. Tool executes. If the caller had skipped step 5,
   `CallerVerificationApprovalHandler` would call `Fail` and the tool would return
   `OnFailureResponse` instead of running.

The DTMF-streaming and NLU paths follow the identical sequence — only step 4 changes:
DTMF collects digits with `CollectDtmf`/in-band detection and invokes the validator tool
through `IvrWorkflowNavigator.InvokeActionAsync` (which runs the step's `Guards` inline
before invoking), and NLU collects digits the same way for the PIN sub-step but falls
back to chat-based intent classification for free-form choices.

### Two-layer gating recap

| Layer | Runs when | Reads from | Enforced by |
|---|---|---|---|
| Step entry | Before `TransitionToStepAsync` allows the move | `IvrWorkflowState.VerificationLevel` (mirrored from `CallerAuthenticationState` by the runner / dispatcher) | `MinimumVerificationGuard` (or any `IIvrStepGuard`) in `step.Guards` / YAML `requires:` |
| Tool entry (per-step) | DTMF menu invocation or model-issued tool call wrapped by `WrapToolsWithCurrentGuards` | Same as above | `GuardedAIFunction` |
| Tool entry (per-tool) | Every model-issued tool call (Realtime + NLU) | `CallerAuthenticationState` resolved from the per-call DI scope | `AuthorizingAgentFunction` + `CallerVerificationApprovalHandler` evaluating `[RequiresCallerVerification(level)]` |

Step-entry guards stop the workflow from *advancing* into privileged territory. Tool-entry
guards stop privileged side-effects even if a caller / model finds a way to invoke them
out of band. Use both — they're cheap and independent.

### Common pitfalls

- **Forgetting `AddCallerAuthenticationTools()`** when expecting the SDK's `validate-pin`
  / `submit-sms-otp` tools to appear. The DI helper is opt-in; without it the host must
  supply its own elevation tools.
- **Adding `[RequiresCallerVerification(MultiFactor)]` to the elevation tool itself.**
  This locks callers out of the path that would have elevated them. Gate elevation tools
  at `AniMatch` (so an identified caller can try) and gate the privileged tools at the
  target level.
- **Skipping the step guard and relying only on the tool attribute.** Without
  `MinimumVerificationGuard` on the step, the workflow can transition into the
  privileged step and the model will narrate the privileged context before the tool
  attribute kicks in. Always gate both.
- **Bypassing the dispatcher from a tool** (calling `state.TryPromote` directly).
  Observers, telemetry spans, and the audit trail on `CallerAuthenticationState.Steps`
  all flow from the dispatcher / orchestrator. Direct mutation skips them and the call
  becomes effectively unobservable from a security-audit standpoint.
- **Treating the DTMF path as a tool-attribute path.** DTMF menu actions go through
  `InvokeActionAsync`, which only evaluates `CurrentStep.Guards` — it does NOT
  re-evaluate the `[RequiresCallerVerification]` attribute (that's a Realtime/NLU
  function-invocation concern). For DTMF flows, always express the requirement as a step
  guard.
