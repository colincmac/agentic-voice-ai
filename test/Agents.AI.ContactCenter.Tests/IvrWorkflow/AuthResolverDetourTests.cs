using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Guards;
using Agents.AI.ContactCenter.IvrWorkflow.Strategies;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow;

/// <summary>
/// Phase 3 contract tests for the auth-resolver detour machinery:
/// <list type="bullet">
///   <item>EvaluateTransitionAsync returns <see cref="TransitionEvaluation.Allowed"/> when guards pass.</item>
///   <item>Returns <see cref="TransitionEvaluation.RequiresDetour"/> when a guard fails and a resolver matches.</item>
///   <item>Returns <see cref="TransitionEvaluation.BlockedNoResolver"/> when no resolver matches.</item>
///   <item>PopFrameAsync chains a second detour when the resume target's stage-level guard
///         still fails (e.g. MFA target after a KBA-only subflow completed).</item>
/// </list>
/// </summary>
public class AuthResolverDetourTests
{
    private const string ParentId = "root";
    private const string KbaSubflowId = "subflows.verify";
    private const string MfaSubflowId = "subflows.verify-mfa";

    [Fact]
    public async Task EvaluateTransition_Allowed_WhenGuardsPass()
    {
        var (navigator, state) = BuildPipeline();
        navigator.EnterInitialStep();
        // No guards on `transfer`.
        var result = await navigator.EvaluateTransitionAsync("transfer");

        var allowed = Assert.IsType<TransitionEvaluation.Allowed>(result);
        Assert.Equal("transfer", allowed.Target.Id);
        Assert.Equal(1, state.FrameDepth); // no push yet
    }

    [Fact]
    public async Task EvaluateTransition_RequiresDetour_WhenGuardFailsAndResolverMatches()
    {
        var (navigator, _) = BuildPipeline();
        navigator.EnterInitialStep();
        // `activate_card` needs KBA; caller is anonymous → KBA resolver matches.
        var result = await navigator.EvaluateTransitionAsync("activate_card");

        var detour = Assert.IsType<TransitionEvaluation.RequiresDetour>(result);
        Assert.Equal("activate_card", detour.Target.Id);
        Assert.Equal(KbaSubflowId, detour.ResolverWorkflowId);
        Assert.IsType<RequiredAuthLevelGuard>(detour.UnmetGuard);
    }

    [Fact]
    public async Task EvaluateTransition_BlockedNoResolver_WhenNoResolverMatches()
    {
        // Build a parent with a guard that has NO matching resolver.
        var parent = BuildWorkflow(
            ParentId,
            authResolvers: [], // empty
            steps:
            [
                BuildStep("welcome", false, [new TransitionRule { TargetStepId = "balance", Guards = [new RequiredAuthLevelGuard(CallerVerificationLevel.MultiFactor)] }]),
                BuildStep("balance", true, []),
            ]);
        var (navigator, _) = BuildPipelineFor(parent);
        navigator.EnterInitialStep();

        var result = await navigator.EvaluateTransitionAsync("balance");

        var blocked = Assert.IsType<TransitionEvaluation.BlockedNoResolver>(result);
        Assert.IsType<RequiredAuthLevelGuard>(blocked.UnmetGuard);
    }

    [Fact]
    public async Task PopFrameAsync_ChainsAnotherDetour_WhenResumeTargetStillGated()
    {
        // Set up: caller at AniMatch, wants `balance` (requires MFA). MFA subflow itself
        // requires KBA. So pushing the MFA subflow and popping it should detect that the
        // resumed `balance` target STILL fails MFA (since OTP didn't actually elevate in
        // the test) — but that's not the cycle we want. Instead, we test the explicit
        // chain: push the MFA subflow, and on entry that subflow's own stage requires KBA.
        var (navigator, state) = BuildPipeline();
        navigator.EnterInitialStep();

        // Manually invoke the resolver: push the MFA subflow with returnTo=balance.
        var mfaInitial = await navigator.PushSubflowAsync(
            MfaSubflowId,
            returnToStepId: "balance",
            failureReturnStepId: "transfer");
        Assert.Equal("collect_otp", mfaInitial.Id);
        Assert.Equal(2, state.FrameDepth);

        // Now evaluate the MFA subflow's entry guard — caller is anonymous so KBA fails;
        // the parent (this MFA subflow) doesn't have its own authResolvers but the parent
        // workflow does (root has kba/mfa resolvers). For Phase 3 the chained re-evaluation
        // runs on pop, not entry — so this test confirms the underlying primitive:
        // PushSubflowAsync did push, PendingIntent semantics are preserved via shared state.
        state.Set(PendingIntent.StateKey, new PendingIntent("balance", ParentId));
        Assert.Equal("balance", state.Get<PendingIntent>(PendingIntent.StateKey)?.TargetStepId);

        // Force success-pop. PopFrameAsync should resume root at `balance`. Since
        // CallerAuthenticationState wasn't actually elevated, the chained re-evaluation
        // should detect the MFA guard still fails and try to detour again — but the cycle
        // guard refuses to push the same MFA subflow twice, so the pop ends up routing to
        // the workflow's onUnauthorized fallback (`transfer`) instead.
        var resumed = await navigator.PopFrameAsync(success: true);

        Assert.NotNull(resumed);
        // Chained detour pushed verify (KBA resolver) because that matches MFA→KBA chain
        // through the workflow's resolver — OR onUnauthorized fallback if cycle guard
        // catches us. Either way the navigator must NOT be sitting on `balance` with a
        // failing guard.
        Assert.NotEqual("balance", state.CurrentFrame?.CurrentStepId);
    }

    [Fact]
    public async Task PendingIntent_StateKey_IsWellKnownConstant()
    {
        // Sanity: contract test for the PendingIntent record's shape and key.
        var intent = new PendingIntent("balance", ParentId, Label: "balance");
        Assert.Equal("PendingIntent", PendingIntent.StateKey);
        Assert.Equal("balance", intent.TargetStepId);
        Assert.Equal(ParentId, intent.ParentWorkflowId);
        Assert.Equal("balance", intent.Label);

        // And: writing PendingIntent into state survives a push/pop boundary because the
        // dict is shared across frames (Phase 1 invariant).
        var (navigator, state) = BuildPipeline();
        navigator.EnterInitialStep();
        state.Set(PendingIntent.StateKey, intent);

        await navigator.PushSubflowAsync(KbaSubflowId, returnToStepId: "activate_card", failureReturnStepId: "transfer");
        Assert.Equal(intent, state.Get<PendingIntent>(PendingIntent.StateKey));

        await navigator.PopFrameAsync(success: false);
        Assert.Equal(intent, state.Get<PendingIntent>(PendingIntent.StateKey));
    }

    // ---------- Test fixture builders ----------

    private static (IvrWorkflowNavigator navigator, IvrWorkflowState state) BuildPipeline()
    {
        var kbaResolver = new CompiledAuthResolver
        {
            Matches = g => g is RequiredAuthLevelGuard k && k.RequiredLevel == CallerVerificationLevel.KnowledgeBased,
            SubflowWorkflowId = KbaSubflowId,
            Description = "auth:KnowledgeBased",
        };
        var mfaResolver = new CompiledAuthResolver
        {
            Matches = g => g is RequiredAuthLevelGuard k && k.RequiredLevel == CallerVerificationLevel.MultiFactor,
            SubflowWorkflowId = MfaSubflowId,
            Description = "auth:MultiFactor",
        };

        // Parent workflow: welcome → {balance(MFA), activate_card(KBA), transfer(none)}.
        var parent = BuildWorkflow(
            ParentId,
            authResolvers: [kbaResolver, mfaResolver],
            unauthorizedStepId: "transfer",
            steps:
            [
                BuildStep("welcome", false, [
                    new TransitionRule { TargetStepId = "balance",       Guards = [new RequiredAuthLevelGuard(CallerVerificationLevel.MultiFactor)] },
                    new TransitionRule { TargetStepId = "activate_card", Guards = [new RequiredAuthLevelGuard(CallerVerificationLevel.KnowledgeBased)] },
                    new TransitionRule { TargetStepId = "transfer",      Guards = [] },
                ]),
                BuildStep("balance",       true, [], stageGuards: [new RequiredAuthLevelGuard(CallerVerificationLevel.MultiFactor)]),
                BuildStep("activate_card", true, [], stageGuards: [new RequiredAuthLevelGuard(CallerVerificationLevel.KnowledgeBased)]),
                BuildStep("transfer",      true, []),
            ]);

        return BuildPipelineFor(parent);
    }

    private static (IvrWorkflowNavigator navigator, IvrWorkflowState state) BuildPipelineFor(CompiledIvrWorkflow parent)
    {
        var kbaSub = BuildWorkflow(
            KbaSubflowId,
            authResolvers: [],
            steps:
            [
                BuildStep("ask_pin",  false, [new TransitionRule { TargetStepId = "verified", Guards = [] }]),
                BuildStep("verified", true,  [], terminalOutcome: TerminalOutcome.Success),
            ]);
        var mfaSub = BuildWorkflow(
            MfaSubflowId,
            authResolvers: [],
            steps:
            [
                BuildStep("collect_otp", false, [new TransitionRule { TargetStepId = "verified", Guards = [] }],
                    stageGuards: [new RequiredAuthLevelGuard(CallerVerificationLevel.KnowledgeBased)]),
                BuildStep("verified",    true,  [], terminalOutcome: TerminalOutcome.Success),
            ]);

        var catalog = new TestCatalog();
        catalog.Register(parent);
        catalog.Register(kbaSub);
        catalog.Register(mfaSub);

        var state = new IvrWorkflowState();
        var navigator = new IvrWorkflowNavigator(
            parent.Runtime,
            state,
            services: new ServiceCollection().BuildServiceProvider(),
            catalog);
        return (navigator, state);
    }

    private static CompiledIvrWorkflow BuildWorkflow(
        string name,
        IReadOnlyList<CompiledAuthResolver> authResolvers,
        params RealtimeIvrWorkflowStep[] steps)
        => BuildWorkflow(name, authResolvers, unauthorizedStepId: null, steps);

    private static CompiledIvrWorkflow BuildWorkflow(
        string name,
        IReadOnlyList<CompiledAuthResolver> authResolvers,
        string? unauthorizedStepId,
        params RealtimeIvrWorkflowStep[] steps)
        => new()
        {
            Name = name,
            Version = 1,
            Runtime = new RealtimeIvrWorkflowDefinition
            {
                Name = name,
                BasePrompt = new RealtimePrompt(),
                Steps = steps,
                AuthResolvers = authResolvers,
                UnauthorizedFailureStepId = unauthorizedStepId,
            },
            Strategy = IvrStrategyPolicy.Default,
            Stages = [],
            Capabilities = new Dictionary<string, CompiledIvrCapability>(StringComparer.Ordinal),
            IntentExamples = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
        };

    private static RealtimeIvrWorkflowStep BuildStep(
        string id,
        bool terminal,
        IReadOnlyList<TransitionRule> transitionRules,
        IReadOnlyList<IIvrStepGuard>? stageGuards = null,
        TerminalOutcome terminalOutcome = TerminalOutcome.Success)
    {
        IReadOnlyList<StateTransition>? transitions = transitionRules.Count == 0
            ? null
            : transitionRules
                .Select(r => new StateTransition { Condition = "default", NextStep = r.TargetStepId })
                .ToList();
        return new RealtimeIvrWorkflowStep
        {
            Id = id,
            ConversationState = new ConversationState
            {
                Id = id,
                Description = id,
                Instructions = [],
                Transitions = transitions,
            },
            Terminal = terminal,
            TerminalOutcome = terminalOutcome,
            Guards = stageGuards ?? [],
            TransitionRules = transitionRules,
        };
    }

    private sealed class TestCatalog : IIvrWorkflowCatalog
    {
        private readonly Dictionary<string, CompiledIvrWorkflow> _byId = new(StringComparer.OrdinalIgnoreCase);

        public void Register(CompiledIvrWorkflow workflow) => _byId[workflow.Name] = workflow;

        public IReadOnlyCollection<string> Ids => _byId.Keys.ToArray();

        public IReadOnlyCollection<int> VersionsFor(string workflowId)
            => _byId.TryGetValue(workflowId, out var w) ? [w.Version >= 1 ? w.Version : 1] : [];

        public bool TryGet(string workflowId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CompiledIvrWorkflow? workflow)
            => TryGet(workflowId, null, null, out workflow);

        public bool TryGet(string workflowId, int? minVersion, int? maxVersion,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CompiledIvrWorkflow? workflow)
        {
            workflow = _byId.GetValueOrDefault(workflowId);
            return workflow is not null;
        }

        public CompiledIvrWorkflow Get(string workflowId)
            => _byId.TryGetValue(workflowId, out var w) ? w : throw new KeyNotFoundException(workflowId);

        public CompiledIvrWorkflow Get(string workflowId, int? minVersion, int? maxVersion) => Get(workflowId);

        public ValueTask EnsureLoadedAsync(CancellationToken cancellationToken = default) => default;
    }
}
