using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Guards;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;
using Agents.AI.ContactCenter.IvrWorkflow.Registry;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow;

/// <summary>
/// Phase 2 compilation tests for the YAML <c>- import: { stage: workflowId.stageId, as: alias }</c>
/// stage type. Exercises the happy path (leaf stage cloned under alias), the leaf-only
/// guard rail (transition-bearing source stages rejected), and the cycle detector.
/// </summary>
public class StageImportCompilationTests
{
    [Fact]
    public void Import_InlinesLeafStage_FromOtherWorkflow()
    {
        using var dir = new TempDir();
        WriteYaml(dir, "lib.yaml", LibClosingYaml);
        WriteYaml(dir, "parent.yaml", ImportingParentYaml);

        var (catalog, _) = BuildPipeline(dir.Path);

        var parent = catalog.Get("parent");
        var closing = parent.Stages.FirstOrDefault(s => s.Id == "closing");

        Assert.NotNull(closing);
        Assert.True(closing!.Terminal);
        // The imported stage should keep its goal/description text from the source.
        Assert.Contains("Goodbye", closing.RuntimeStep.ConversationState.Instructions[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Import_FailsWhen_SourceStageHasTransitions()
    {
        using var dir = new TempDir();
        WriteYaml(dir, "lib.yaml", LibWithTransitionsYaml);
        WriteYaml(dir, "parent.yaml", ImportingParentYaml.Replace("lib.closing", "lib.middle"));

        var (catalog, _) = BuildPipeline(dir.Path);

        var ex = Assert.Throws<IvrWorkflowCompilationException>(() => catalog.Get("parent"));
        Assert.Contains("outbound transition", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_FailsOn_Cycle()
    {
        using var dir = new TempDir();
        WriteYaml(dir, "a.yaml", CycleAYaml);
        WriteYaml(dir, "b.yaml", CycleBYaml);

        var (catalog, _) = BuildPipeline(dir.Path);

        var ex = Assert.Throws<IvrWorkflowCompilationException>(() => catalog.Get("a"));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_ResolvesSingleStageWorkflow_ByWorkflowIdAlone()
    {
        // The reference "lib" is the full workflow id. The workflow has exactly one stage,
        // so the compiler should pick it without requiring "lib.<stageId>".
        using var dir = new TempDir();
        WriteYaml(dir, "lib.yaml", LibClosingYaml);
        WriteYaml(dir, "parent.yaml", ImportingParentYaml.Replace("lib.closing", "lib"));

        var (catalog, _) = BuildPipeline(dir.Path);

        var parent = catalog.Get("parent");
        var closing = parent.Stages.FirstOrDefault(s => s.Id == "closing");

        Assert.NotNull(closing);
        Assert.True(closing!.Terminal);
        Assert.Contains("Goodbye", closing.RuntimeStep.ConversationState.Instructions[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Import_RejectsBareReference_WhenWorkflowHasMultipleStages()
    {
        // "lib" matches a workflow that has 2+ stages → the compiler must refuse to guess
        // and surface a message instructing the author to qualify the stage id.
        using var dir = new TempDir();
        WriteYaml(dir, "lib.yaml", LibWithTwoStagesYaml);
        WriteYaml(dir, "parent.yaml", ImportingParentYaml.Replace("lib.closing", "lib"));

        var (catalog, _) = BuildPipeline(dir.Path);

        var ex = Assert.Throws<IvrWorkflowCompilationException>(() => catalog.Get("parent"));
        Assert.Contains("specify the stage id explicitly", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_LongestPrefixWins_ForMultiSegmentWorkflowIds()
    {
        // Two workflows registered: "banking" (single-stage) and "banking.lib" (with stage
        // "closing"). The reference "banking.lib.closing" must resolve via longest-prefix
        // to workflow "banking.lib" + stage "closing", not workflow "banking" + (missing) stage.
        using var dir = new TempDir();
        WriteYaml(dir, "banking.yaml", BankingRootYaml);
        // FileSystemWorkflowSource derives ids by replacing path separators with '.', so
        // banking/lib.yaml becomes workflow id "banking.lib".
        Directory.CreateDirectory(Path.Combine(dir.Path, "banking"));
        File.WriteAllText(Path.Combine(dir.Path, "banking", "lib.yaml"), BankingLibYaml);
        WriteYaml(dir, "parent.yaml", ImportingParentYaml.Replace("lib.closing", "banking.lib.closing"));

        var (catalog, _) = BuildPipeline(dir.Path);

        var parent = catalog.Get("parent");
        var closing = parent.Stages.FirstOrDefault(s => s.Id == "closing");

        Assert.NotNull(closing);
        Assert.True(closing!.Terminal);
        Assert.Contains("Goodbye from banking.lib", closing.RuntimeStep.ConversationState.Instructions[0], StringComparison.Ordinal);
    }

    private static (IIvrWorkflowCatalog Catalog, IIvrWorkflowLoader Loader) BuildPipeline(string root)
    {
        var tools = new IvrToolRegistry();
        var predicates = new IvrPredicateRegistry();

        IIvrWorkflowCatalog? catalogRef = null;
        var compiler = new IvrWorkflowCompiler(
            tools,
            predicates,
            guardFactories: null,
            catalogAccessor: () => catalogRef!);

        var source = new FileSystemWorkflowSource(root);
        var loader = new IvrWorkflowLoader(source, compiler);
        var catalog = new IvrWorkflowCatalog(loader);
        catalogRef = catalog;

        return (catalog, loader);
    }

    private static void WriteYaml(TempDir dir, string fileName, string contents)
        => File.WriteAllText(Path.Combine(dir.Path, fileName), contents);

    private const string LibClosingYaml = @"
name: lib
stages:
  - id: closing
    terminal: true
    realtime:
      instructions:
        - 'Goodbye and thank you.'
";

    private const string LibWithTwoStagesYaml = @"
name: lib
stages:
  - id: closing
    terminal: true
    realtime:
      instructions:
        - 'Goodbye and thank you.'
  - id: other
    terminal: true
    realtime:
      instructions:
        - 'Another leaf.'
";

    private const string BankingRootYaml = @"
name: banking
stages:
  - id: root
    terminal: true
    realtime:
      instructions:
        - 'Banking root workflow.'
";

    private const string BankingLibYaml = @"
name: banking.lib
stages:
  - id: closing
    terminal: true
    realtime:
      instructions:
        - 'Goodbye from banking.lib.'
";

    private const string LibWithTransitionsYaml = @"
name: lib
stages:
  - id: middle
    realtime:
      instructions:
        - 'Has a transition, cannot be imported.'
    transitions:
      - to: somewhere_else
        onCondition: 'always'
  - id: somewhere_else
    terminal: true
    realtime:
      instructions:
        - 'Endpoint.'
";

    private const string ImportingParentYaml = @"
name: parent
stages:
  - id: greet
    realtime:
      instructions:
        - 'Say hi.'
    transitions:
      - to: closing
        onCondition: 'done'
  - import:
      stage: lib.closing
      as: closing
";

    // a imports a stage from b; b imports a stage from a → cycle.
    private const string CycleAYaml = @"
name: a
stages:
  - id: start
    realtime:
      instructions: ['start']
    transitions:
      - to: from_b
        onCondition: 'next'
  - import:
      stage: b.shared
      as: from_b
";

    private const string CycleBYaml = @"
name: b
stages:
  - id: shared
    realtime:
      instructions: ['shared']
    transitions:
      - to: from_a
        onCondition: 'next'
  - import:
      stage: a.start
      as: from_a
";

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ivr-import-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
