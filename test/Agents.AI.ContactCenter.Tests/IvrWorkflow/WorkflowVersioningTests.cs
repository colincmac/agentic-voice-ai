using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;
using Agents.AI.ContactCenter.IvrWorkflow.Strategies;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow;

/// <summary>
/// Phase 2 contract tests for workflow versioning: filename @N parsing on
/// <see cref="FileSystemWorkflowSource"/>, version-pinned <see cref="IvrWorkflowCatalog"/>
/// lookups, and version-constrained subflow pushes through the navigator.
/// </summary>
public class WorkflowVersioningTests
{
    [Fact]
    public async Task FileSystemSource_Parses_AtVersionSuffix()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "alpha.yaml"), MinimalYaml("alpha"));
        File.WriteAllText(Path.Combine(dir.Path, "alpha@2.yaml"), MinimalYaml("alpha"));
        File.WriteAllText(Path.Combine(dir.Path, "alpha@3.yaml"), MinimalYaml("alpha"));

        var source = new FileSystemWorkflowSource(dir.Path);

        var ids = await source.ListAsync();
        Assert.Single(ids);
        Assert.Equal("alpha", ids[0]);

        var versions = await source.ListVersionsAsync("alpha");
        Assert.Equal(new[] { 1, 2, 3 }, versions);

        var latest = await source.LoadAsync("alpha");
        Assert.NotNull(latest);
        Assert.Equal(3, latest!.Version);

        var pinned = await source.LoadAsync("alpha", version: 2);
        Assert.NotNull(pinned);
        Assert.Equal(2, pinned!.Version);

        var missing = await source.LoadAsync("alpha", version: 99);
        Assert.Null(missing);
    }

    [Fact]
    public void Catalog_LatestWins_AndPinSelectsExactVersion()
    {
        var loader = new VersionedFakeLoader();
        loader.Register("alpha", BuildWorkflow("alpha", 1));
        loader.Register("alpha", BuildWorkflow("alpha", 2));
        loader.Register("alpha", BuildWorkflow("alpha", 4));
        var catalog = new IvrWorkflowCatalog(loader);

        Assert.True(catalog.TryGet("alpha", out var latest));
        Assert.Equal(4, latest!.Version);

        Assert.True(catalog.TryGet("alpha", minVersion: 2, maxVersion: 2, out var pinned));
        Assert.Equal(2, pinned!.Version);

        Assert.True(catalog.TryGet("alpha", minVersion: 1, maxVersion: 3, out var range));
        Assert.Equal(2, range!.Version); // highest in [1, 3]

        Assert.False(catalog.TryGet("alpha", minVersion: 5, maxVersion: null, out _));
        Assert.False(catalog.TryGet("alpha", minVersion: null, maxVersion: 0, out _));
    }

    [Fact]
    public async Task Catalog_EnsureLoaded_PopulatesAllVersions()
    {
        var loader = new VersionedFakeLoader();
        loader.Register("beta", BuildWorkflow("beta", 1));
        loader.Register("beta", BuildWorkflow("beta", 2));
        loader.Register("gamma", BuildWorkflow("gamma", 5));
        var catalog = new IvrWorkflowCatalog(loader);

        await catalog.EnsureLoadedAsync();

        Assert.Equal(2, catalog.Ids.Count);
        Assert.Equal(new[] { 1, 2 }, catalog.VersionsFor("beta"));
        Assert.Equal(new[] { 5 }, catalog.VersionsFor("gamma"));
    }

    [Fact]
    public async Task PushSubflow_HonorsVersionPin()
    {
        // Build a parent navigator backed by an in-memory catalog with two child versions.
        var parent = BuildWorkflow("root", 1, new (string, bool, string[])[]
        {
            ("root_start", false, new[] { "after" }),
            ("after", true, Array.Empty<string>()),
        });
        var childV1 = BuildWorkflow("child", 1, new (string, bool, string[])[]
        {
            ("c1_start", true, Array.Empty<string>()),
        });
        var childV3 = BuildWorkflow("child", 3, new (string, bool, string[])[]
        {
            ("c3_start", true, Array.Empty<string>()),
        });

        var catalog = new MultiVersionInMemoryCatalog();
        catalog.Register(parent);
        catalog.Register(childV1);
        catalog.Register(childV3);

        var state = new IvrWorkflowState();
        var navigator = new IvrWorkflowNavigator(
            parent.Runtime,
            state,
            services: new ServiceCollection().BuildServiceProvider(),
            catalog);
        navigator.EnterInitialStep();

        // Pin to version 1 even though 3 is available.
        var initial = await navigator.PushSubflowAsync(
            "child",
            returnToStepId: "after",
            failureReturnStepId: null,
            minVersion: 1,
            maxVersion: 1);

        Assert.Equal("c1_start", initial.Id);
        Assert.Equal("child", state.CurrentFrame!.WorkflowId);
        Assert.Equal(1, state.CurrentFrame.WorkflowVersion);
    }

    [Fact]
    public async Task PushSubflow_ThrowsWhenNoVersionMatchesPin()
    {
        var parent = BuildWorkflow("root", 1, new (string, bool, string[])[]
        {
            ("root_start", false, new[] { "after" }),
            ("after", true, Array.Empty<string>()),
        });
        var childV1 = BuildWorkflow("child", 1, new (string, bool, string[])[]
        {
            ("c1_start", true, Array.Empty<string>()),
        });
        var catalog = new MultiVersionInMemoryCatalog();
        catalog.Register(parent);
        catalog.Register(childV1);

        var state = new IvrWorkflowState();
        var navigator = new IvrWorkflowNavigator(
            parent.Runtime, state, new ServiceCollection().BuildServiceProvider(), catalog);
        navigator.EnterInitialStep();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            navigator.PushSubflowAsync("child", returnToStepId: "after", failureReturnStepId: null, minVersion: 5, maxVersion: 5));
    }

    private static string MinimalYaml(string name) => $@"
name: {name}
stages:
  - id: only
    terminal: true
    realtime:
      instructions: ['placeholder']
";

    private static CompiledIvrWorkflow BuildWorkflow(string name, int version, params (string id, bool terminal, string[] transitions)[] steps)
    {
        var stepList = new List<RealtimeIvrWorkflowStep>();
        foreach (var (id, terminal, transitions) in steps)
        {
            var ts = transitions.Length == 0
                ? null
                : (IReadOnlyList<StateTransition>)transitions
                    .Select(t => new StateTransition { Condition = "default", NextStep = t })
                    .ToList();
            stepList.Add(new RealtimeIvrWorkflowStep
            {
                Id = id,
                ConversationState = new ConversationState
                {
                    Id = id,
                    Description = id,
                    Instructions = [],
                    Transitions = ts,
                },
                Terminal = terminal,
            });
        }

        if (stepList.Count == 0)
        {
            stepList.Add(new RealtimeIvrWorkflowStep
            {
                Id = "only",
                ConversationState = new ConversationState { Id = "only", Description = name, Instructions = [] },
                Terminal = true,
            });
        }

        var runtime = new RealtimeIvrWorkflowDefinition
        {
            Name = name,
            BasePrompt = new RealtimePrompt(),
            Steps = stepList,
        };
        return new CompiledIvrWorkflow
        {
            Name = name,
            Runtime = runtime,
            Version = version,
            Strategy = IvrStrategyPolicy.Default,
            Stages = [],
            Capabilities = new Dictionary<string, CompiledIvrCapability>(StringComparer.Ordinal),
            IntentExamples = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
        };
    }

    private sealed class VersionedFakeLoader : IIvrWorkflowLoader
    {
        // id → sorted version → compiled workflow
        private readonly Dictionary<string, SortedDictionary<int, CompiledIvrWorkflow>> _byId
            = new(StringComparer.OrdinalIgnoreCase);

        public void Register(string id, CompiledIvrWorkflow workflow)
        {
            if (!_byId.TryGetValue(id, out var versions))
            {
                versions = new SortedDictionary<int, CompiledIvrWorkflow>();
                _byId[id] = versions;
            }
            versions[workflow.Version] = workflow;
        }

        public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<string>>(_byId.Keys.ToArray());

        public ValueTask<IReadOnlyList<int>> ListVersionsAsync(string workflowId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<int>>(
                _byId.TryGetValue(workflowId, out var versions) ? versions.Keys.ToArray() : []);

        public ValueTask<CompiledIvrWorkflow> LoadAsync(string workflowId, CancellationToken cancellationToken = default)
            => LoadAsync(workflowId, version: null, cancellationToken);

        public ValueTask<CompiledIvrWorkflow> LoadAsync(string workflowId, int? version, CancellationToken cancellationToken = default)
        {
            if (!_byId.TryGetValue(workflowId, out var versions) || versions.Count == 0)
            {
                throw new IvrWorkflowYamlException($"no '{workflowId}'");
            }
            if (version is null)
            {
                return ValueTask.FromResult(versions[versions.Keys.Last()]);
            }
            if (!versions.TryGetValue(version.Value, out var workflow))
            {
                throw new IvrWorkflowYamlException($"no '{workflowId}' v{version}");
            }
            return ValueTask.FromResult(workflow);
        }

        public CompiledIvrWorkflow Compile(string yaml, string? sourceName = null)
            => throw new NotSupportedException();
    }

    /// <summary>Catalog test fake that stores multiple versions per id without going through a loader.</summary>
    private sealed class MultiVersionInMemoryCatalog : IIvrWorkflowCatalog
    {
        private readonly Dictionary<string, SortedDictionary<int, CompiledIvrWorkflow>> _byId
            = new(StringComparer.OrdinalIgnoreCase);

        public void Register(CompiledIvrWorkflow workflow)
        {
            if (!_byId.TryGetValue(workflow.Name, out var versions))
            {
                versions = new SortedDictionary<int, CompiledIvrWorkflow>();
                _byId[workflow.Name] = versions;
            }
            versions[workflow.Version] = workflow;
        }

        public IReadOnlyCollection<string> Ids => _byId.Keys.ToArray();

        public IReadOnlyCollection<int> VersionsFor(string workflowId)
            => _byId.TryGetValue(workflowId, out var versions) ? versions.Keys.ToArray() : [];

        public bool TryGet(string workflowId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CompiledIvrWorkflow? workflow)
            => TryGet(workflowId, null, null, out workflow);

        public bool TryGet(string workflowId, int? minVersion, int? maxVersion,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CompiledIvrWorkflow? workflow)
        {
            if (_byId.TryGetValue(workflowId, out var versions))
            {
                foreach (var pair in versions.Reverse())
                {
                    if ((minVersion is null || pair.Key >= minVersion) && (maxVersion is null || pair.Key <= maxVersion))
                    {
                        workflow = pair.Value;
                        return true;
                    }
                }
            }
            workflow = null;
            return false;
        }

        public CompiledIvrWorkflow Get(string workflowId) => Get(workflowId, null, null);

        public CompiledIvrWorkflow Get(string workflowId, int? minVersion, int? maxVersion)
            => TryGet(workflowId, minVersion, maxVersion, out var w) ? w : throw new KeyNotFoundException(workflowId);

        public ValueTask EnsureLoadedAsync(CancellationToken cancellationToken = default) => default;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ivr-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
