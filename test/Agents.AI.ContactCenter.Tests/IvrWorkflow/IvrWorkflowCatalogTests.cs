using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;
using Agents.AI.ContactCenter.IvrWorkflow.Strategies;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow;

/// <summary>
/// Contract tests for the Phase 1 <see cref="IvrWorkflowCatalog"/>. Verifies lazy
/// load via the underlying <see cref="IIvrWorkflowLoader"/>, caching, missing-id
/// behavior, and <c>EnsureLoadedAsync</c> enumeration.
/// </summary>
public class IvrWorkflowCatalogTests
{
    [Fact]
    public void TryGet_Lazy_LoadsViaLoader_AndCaches()
    {
        var loader = new FakeLoader();
        loader.Register("alpha", BuildWorkflow("alpha"));
        var catalog = new IvrWorkflowCatalog(loader);

        Assert.True(catalog.TryGet("alpha", out var first));
        Assert.Equal("alpha", first!.Name);
        Assert.Equal(1, loader.LoadCalls);

        Assert.True(catalog.TryGet("alpha", out var second));
        Assert.Same(first, second);
        Assert.Equal(1, loader.LoadCalls); // cached, not re-loaded
    }

    [Fact]
    public void TryGet_ReturnsFalse_WhenLoaderThrows()
    {
        var loader = new FakeLoader();
        var catalog = new IvrWorkflowCatalog(loader);

        Assert.False(catalog.TryGet("missing", out var workflow));
        Assert.Null(workflow);
    }

    [Fact]
    public void Get_Throws_WhenWorkflowMissing()
    {
        var catalog = new IvrWorkflowCatalog(new FakeLoader());

        var ex = Assert.Throws<KeyNotFoundException>(() => catalog.Get("nope"));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task EnsureLoadedAsync_PopulatesAllKnownIds()
    {
        var loader = new FakeLoader();
        loader.Register("a", BuildWorkflow("a"));
        loader.Register("b", BuildWorkflow("b"));
        loader.Register("c", BuildWorkflow("c"));
        var catalog = new IvrWorkflowCatalog(loader);

        await catalog.EnsureLoadedAsync();

        Assert.Equal(3, catalog.Ids.Count);
        Assert.Contains("a", catalog.Ids);
        Assert.Contains("b", catalog.Ids);
        Assert.Contains("c", catalog.Ids);

        // Idempotent — second call doesn't re-enumerate.
        await catalog.EnsureLoadedAsync();
        Assert.Equal(1, loader.ListCalls);
    }

    private static CompiledIvrWorkflow BuildWorkflow(string name)
    {
        var step = new RealtimeIvrWorkflowStep
        {
            Id = "start",
            ConversationState = new ConversationState
            {
                Id = "start",
                Description = name,
                Instructions = [],
            },
            Terminal = true,
        };
        var runtime = new RealtimeIvrWorkflowDefinition
        {
            Name = name,
            BasePrompt = new RealtimePrompt(),
            Steps = [step],
        };
        return new CompiledIvrWorkflow
        {
            Name = name,
            Runtime = runtime,
            Strategy = IvrStrategyPolicy.Default,
            Stages = [],
            Capabilities = new Dictionary<string, CompiledIvrCapability>(StringComparer.Ordinal),
            IntentExamples = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
        };
    }

    private sealed class FakeLoader : IIvrWorkflowLoader
    {
        private readonly Dictionary<string, CompiledIvrWorkflow> _byId = new(StringComparer.OrdinalIgnoreCase);
        public int LoadCalls { get; private set; }
        public int ListCalls { get; private set; }

        public void Register(string id, CompiledIvrWorkflow workflow) => _byId[id] = workflow;

        public CompiledIvrWorkflow Compile(string yaml, string? sourceName = null)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return ValueTask.FromResult<IReadOnlyList<string>>(_byId.Keys.ToArray());
        }

        public ValueTask<IReadOnlyList<int>> ListVersionsAsync(string workflowId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<int>>(_byId.ContainsKey(workflowId) ? [1] : []);

        public ValueTask<CompiledIvrWorkflow> LoadAsync(string workflowId, CancellationToken cancellationToken = default)
            => LoadAsync(workflowId, version: null, cancellationToken);

        public ValueTask<CompiledIvrWorkflow> LoadAsync(string workflowId, int? version, CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            if (!_byId.TryGetValue(workflowId, out var workflow))
            {
                throw new IvrWorkflowYamlException($"no '{workflowId}'");
            }
            return ValueTask.FromResult(workflow);
        }
    }
}
