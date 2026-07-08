using global::Agents.AI.ContactCenter.Authentication;
using global::Agents.AI.ContactCenter.IvrWorkflow;
using global::Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using global::Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using global::Agents.AI.ContactCenter.IvrWorkflow.Predicates;
using global::Agents.AI.ContactCenter.IvrWorkflow.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Compilation;

public sealed class WorkflowGraphCompilerTests
{
    private static WorkflowBlueprint AcmeBankBlueprint() => new()
    {
        Id = "acme-bank",
        Description = "Simple bank concierge.",
        InitialStageId = "welcome",
        Stages =
        [
            new StageBlueprint
            {
                Id = "welcome",
                Goal = "Greet caller and capture intent.",
                Transitions =
                [
                    new TransitionBlueprint
                    {
                        TargetStageId = "balance",
                        Label = "balance",
                        When = "Caller wants to hear their balance.",
                        Requires = [PredicateRef.AuthVerificationLevel(CallerVerificationLevel.MultiFactor)],
                        OnBlockedStageId = "verify",
                    },
                    new TransitionBlueprint
                    {
                        TargetStageId = "transfer",
                        Label = "agent",
                    },
                ],
            },
            new StageBlueprint
            {
                Id = "verify",
                Goal = "Collect PIN + OTP.",
                Transitions =
                [
                    new TransitionBlueprint { TargetStageId = "balance", Label = "verified" },
                ],
            },
            new StageBlueprint { Id = "balance", Terminal = true, TerminalOutcome = BlueprintTerminalOutcome.Success },
            new StageBlueprint { Id = "transfer", Terminal = true, TerminalOutcome = BlueprintTerminalOutcome.Escalated },
        ],
    };

    [Fact]
    public void Compile_HappyPath_ProducesAllStagesAndEdges()
    {
        var compiler = new WorkflowGraphCompiler();
        var compiled = compiler.Compile(AcmeBankBlueprint());

        Assert.Equal("acme-bank", compiled.Id);
        Assert.Equal(4, compiled.Stages.Count);
        Assert.Equal("welcome", compiled.InitialStage.Id);

        var welcome = compiled.GetStage("welcome");
        Assert.Equal(2, welcome.OutgoingEdges.Count);
        Assert.Equal("balance", welcome.OutgoingEdges[0].TargetStageId);
        Assert.Equal("verify", welcome.OutgoingEdges[0].OnBlockedStageId);

        Assert.True(compiled.GetStage("balance").Terminal);
        Assert.Empty(compiled.GetStage("balance").OutgoingEdges);
    }

    [Fact]
    public void Compile_FailsOnUnknownTransitionTarget()
    {
        var blueprint = new WorkflowBlueprint
        {
            Id = "bad",
            InitialStageId = "welcome",
            Stages =
            [
                new StageBlueprint
                {
                    Id = "welcome",
                    Transitions = [new TransitionBlueprint { TargetStageId = "missing" }],
                },
            ],
        };

        var ex = Assert.Throws<WorkflowCompilationException>(() => new WorkflowGraphCompiler().Compile(blueprint));
        Assert.Contains("unknown stage 'missing'", string.Join(';', ex.Errors));
    }

    [Fact]
    public void Compile_FailsOnUnknownOnBlocked()
    {
        var blueprint = new WorkflowBlueprint
        {
            Id = "bad",
            InitialStageId = "welcome",
            Stages =
            [
                new StageBlueprint
                {
                    Id = "welcome",
                    Transitions =
                    [
                        new TransitionBlueprint
                        {
                            TargetStageId = "next",
                            OnBlockedStageId = "missing-fallback",
                        },
                    ],
                },
                new StageBlueprint { Id = "next", Terminal = true },
            ],
        };

        var ex = Assert.Throws<WorkflowCompilationException>(() => new WorkflowGraphCompiler().Compile(blueprint));
        Assert.Contains("missing-fallback", string.Join(';', ex.Errors));
    }

    [Fact]
    public void Compile_FailsOnDuplicateStageId()
    {
        var blueprint = new WorkflowBlueprint
        {
            Id = "bad",
            InitialStageId = "a",
            Stages =
            [
                new StageBlueprint { Id = "a" },
                new StageBlueprint { Id = "a" },
            ],
        };

        var ex = Assert.Throws<WorkflowCompilationException>(() => new WorkflowGraphCompiler().Compile(blueprint));
        Assert.Contains("Duplicate stage id 'a'", string.Join(';', ex.Errors));
    }

    [Fact]
    public void Compile_FailsOnMissingInitialStage()
    {
        var blueprint = new WorkflowBlueprint
        {
            Id = "bad",
            InitialStageId = "missing",
            Stages = [new StageBlueprint { Id = "only" }],
        };

        var ex = Assert.Throws<WorkflowCompilationException>(() => new WorkflowGraphCompiler().Compile(blueprint));
        Assert.Contains("InitialStageId 'missing'", string.Join(';', ex.Errors));
    }

    [Fact]
    public async Task Compile_AuthPredicate_IsEvaluatedAtRuntime()
    {
        var blueprint = new WorkflowBlueprint
        {
            Id = "auth",
            InitialStageId = "a",
            Stages =
            [
                new StageBlueprint
                {
                    Id = "a",
                    Transitions =
                    [
                        new TransitionBlueprint
                        {
                            TargetStageId = "b",
                            Requires = [PredicateRef.AuthVerificationLevel(CallerVerificationLevel.MultiFactor)],
                        },
                    ],
                },
                new StageBlueprint { Id = "b", Terminal = true },
            ],
        };

        var compiled = new WorkflowGraphCompiler().Compile(blueprint);
        var edge = compiled.GetStage("a").OutgoingEdges[0];

        var sp = new ServiceCollection().BuildServiceProvider();
        var deny = await edge.Predicate(new WorkflowEdgeContext(new IvrWorkflowState(), null), default);
        Assert.False(deny.Passed);

        var auth = new CallerAuthenticationState();
        auth.TryPromote(CallerIdentity.Anonymous with
        {
            UserId = "u",
            VerificationLevel = CallerVerificationLevel.MultiFactor,
        });
        var allow = await edge.Predicate(new WorkflowEdgeContext(new IvrWorkflowState(), auth), default);
        Assert.True(allow.Passed);
    }

    [Fact]
    public async Task Compile_NamedPredicate_ResolvesThroughProvider()
    {
        var services = new ServiceCollection();
        services.AddNamedEdgePredicate("isVip", (_, _) => ValueTask.FromResult(EdgePredicateResult.Allow()));
        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<INamedEdgePredicateProvider>();

        var blueprint = new WorkflowBlueprint
        {
            Id = "vip",
            InitialStageId = "a",
            Stages =
            [
                new StageBlueprint
                {
                    Id = "a",
                    Transitions =
                    [
                        new TransitionBlueprint
                        {
                            TargetStageId = "b",
                            Requires = [PredicateRef.Named("isVip")],
                        },
                    ],
                },
                new StageBlueprint { Id = "b", Terminal = true },
            ],
        };

        var compiled = new WorkflowGraphCompiler(provider).Compile(blueprint);
        var edge = compiled.GetStage("a").OutgoingEdges[0];

        var result = await edge.Predicate(new WorkflowEdgeContext(new IvrWorkflowState(), null), default);
        Assert.True(result.Passed);
    }

    [Fact]
    public void Compile_NamedPredicate_FailsWhenProviderMissing()
    {
        var blueprint = new WorkflowBlueprint
        {
            Id = "vip",
            InitialStageId = "a",
            Stages =
            [
                new StageBlueprint
                {
                    Id = "a",
                    Transitions =
                    [
                        new TransitionBlueprint
                        {
                            TargetStageId = "b",
                            Requires = [PredicateRef.Named("isVip")],
                        },
                    ],
                },
                new StageBlueprint { Id = "b", Terminal = true },
            ],
        };

        // No provider, but a named ref is declared — should surface as a compilation error.
        var ex = Assert.Throws<WorkflowCompilationException>(() => new WorkflowGraphCompiler().Compile(blueprint));
        Assert.Contains("isVip", string.Join(';', ex.Errors));
    }

    [Fact]
    public void Compile_AggregatesEveryErrorIntoOneException()
    {
        var blueprint = new WorkflowBlueprint
        {
            Id = "bad",
            InitialStageId = "missing",
            Stages =
            [
                new StageBlueprint { Id = "a" },
                new StageBlueprint { Id = "a" },
            ],
        };

        var ex = Assert.Throws<WorkflowCompilationException>(() => new WorkflowGraphCompiler().Compile(blueprint));
        Assert.True(ex.Errors.Count >= 2, $"Expected aggregate errors, got {ex.Errors.Count}.");
    }

    [Fact]
    public void Stage_FindEdgeBy_ReturnsExpected()
    {
        var compiled = new WorkflowGraphCompiler().Compile(AcmeBankBlueprint());
        var welcome = compiled.GetStage("welcome");

        Assert.NotNull(welcome.FindEdgeTo("balance"));
        Assert.Null(welcome.FindEdgeTo("nope"));
        Assert.NotNull(welcome.FindEdgeByLabel("agent"));
        Assert.Null(welcome.FindEdgeByLabel("missing"));
    }

    // ------------------------------------------------------------------
    // Tool-binding resolution
    // ------------------------------------------------------------------

    private static AIFunction StubFunction(string name) =>
        AIFunctionFactory.Create(() => $"hi from {name}", name);

    private static IIvrToolRegistry BuildRegistry(string agentKey, params string[] toolNames)
    {
        var builder = new IvrToolRegistryBuilder(agentKey);
        foreach (var name in toolNames)
        {
            builder.Add(new ToolBinding(name, ServiceLifetime.Singleton, _ => StubFunction(name)));
        }
        return builder.Build();
    }

    private static WorkflowBlueprint BlueprintWithToolReferences(
        IReadOnlyList<string> commonTools,
        IReadOnlyList<string> stageTools,
        IReadOnlyList<string>? realtimeTools = null)
        => new()
        {
            Id = "with-tools",
            InitialStageId = "only",
            CommonToolNames = commonTools,
            Stages =
            [
                new StageBlueprint
                {
                    Id = "only",
                    Goal = "Demo stage with tools.",
                    ToolNames = stageTools,
                    Channels = new StageChannelConfig
                    {
                        Realtime = realtimeTools is null
                            ? null
                            : new StageRealtimePrompt { ToolNames = realtimeTools },
                    },
                },
            ],
        };

    [Fact]
    public void Compile_WithRegistry_PopulatesStageToolBindings()
    {
        var registry = BuildRegistry("triage", "alpha", "beta", "gamma");
        var blueprint = BlueprintWithToolReferences(
            commonTools: new[] { "alpha" },
            stageTools: new[] { "beta" },
            realtimeTools: new[] { "gamma" });

        var compiled = new WorkflowGraphCompiler(toolRegistry: registry).Compile(blueprint);
        var stage = compiled.GetStage("only");

        Assert.Equal(3, stage.ToolBindings.Count);
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, stage.ToolBindings.Select(b => b.Name));
    }

    [Fact]
    public void Compile_WithRegistry_MissingToolName_ThrowsAggregateException()
    {
        var registry = BuildRegistry("triage", "alpha");
        var blueprint = BlueprintWithToolReferences(
            commonTools: new[] { "alpha", "missing-common" },
            stageTools: new[] { "missing-stage" });

        var ex = Assert.Throws<WorkflowCompilationException>(() =>
            new WorkflowGraphCompiler(toolRegistry: registry).Compile(blueprint));

        var joined = string.Join(';', ex.Errors);
        Assert.Contains("missing-common", joined);
        Assert.Contains("missing-stage", joined);
        Assert.DoesNotContain("alpha", joined);
    }

    [Fact]
    public void Compile_DedupesAcrossCommonAndStageAndRealtime_PreservingOrder()
    {
        var registry = BuildRegistry("triage", "alpha", "beta", "gamma");
        var blueprint = BlueprintWithToolReferences(
            commonTools: new[] { "alpha", "beta" },
            stageTools: new[] { "beta", "gamma" }, // 'beta' duplicate from common — dedupes.
            realtimeTools: new[] { "alpha", "gamma" }); // both already seen — dedupes.

        var compiled = new WorkflowGraphCompiler(toolRegistry: registry).Compile(blueprint);
        var stage = compiled.GetStage("only");

        // Author order preserved: common ('alpha', 'beta'), then stage's new tools ('gamma'),
        // then realtime's new tools (none — both already seen).
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, stage.ToolBindings.Select(b => b.Name));
    }

    [Fact]
    public void Compile_WithNullRegistry_LeavesToolBindingsEmpty()
    {
        var blueprint = BlueprintWithToolReferences(
            commonTools: new[] { "would-not-resolve" },
            stageTools: new[] { "also-ignored" });

        // toolRegistry = null is the legacy / test path: no validation, no resolution.
        var compiled = new WorkflowGraphCompiler().Compile(blueprint);
        var stage = compiled.GetStage("only");

        Assert.Empty(stage.ToolBindings);
    }
}
