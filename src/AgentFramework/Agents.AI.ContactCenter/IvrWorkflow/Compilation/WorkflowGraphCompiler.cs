using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Predicates;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>
/// Thrown when <see cref="WorkflowGraphCompiler"/> can't produce a <see cref="CompiledCallWorkflow"/>
/// from a <see cref="WorkflowBlueprint"/>. The exception aggregates every validation error so
/// authors can fix them all in one pass.
/// </summary>
public sealed class WorkflowCompilationException(string workflowId, IReadOnlyList<string> errors)
    : InvalidOperationException(BuildMessage(workflowId, errors))
{
    public string WorkflowId { get; } = workflowId;
    public IReadOnlyList<string> Errors { get; } = errors;

    private static string BuildMessage(string workflowId, IReadOnlyList<string> errors)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);
        ArgumentNullException.ThrowIfNull(errors);
        return $"Workflow '{workflowId}' failed to compile with {errors.Count} error(s):" +
            Environment.NewLine +
            string.Join(Environment.NewLine, errors.Select(e => "  - " + e));
    }
}

/// <summary>
/// Translates a <see cref="WorkflowBlueprint"/> into a runtime <see cref="CompiledCallWorkflow"/>.
/// Resolves every <see cref="PredicateRef"/> against the built-in factories (and, for
/// <see cref="PredicateKind.Named"/>, the supplied <see cref="INamedEdgePredicateProvider"/>),
/// validates graph structure (no duplicate ids, every transition target exists, every
/// <see cref="TransitionBlueprint.OnBlockedStageId"/> exists, an initial stage exists), and
/// produces immutable <see cref="CompiledStage"/> nodes with pre-built edge predicates.
/// </summary>
/// <remarks>
/// Tool-name validation is intentionally <em>not</em> performed here — tools resolve through
/// keyed DI per call and the registered set may differ between the compile-time host scope
/// and the per-call scope. Strategies should fail fast on missing tool names when they
/// build the per-stage tool surface.
/// </remarks>
public sealed class WorkflowGraphCompiler
{
    private readonly INamedEdgePredicateProvider? _namedPredicates;

    /// <summary>Construct a compiler. Pass a provider to enable <see cref="PredicateKind.Named"/> references.</summary>
    public WorkflowGraphCompiler(INamedEdgePredicateProvider? namedPredicates = null)
    {
        _namedPredicates = namedPredicates;
    }

    /// <summary>Compile <paramref name="blueprint"/>. Throws <see cref="WorkflowCompilationException"/> on any validation error.</summary>
    public CompiledCallWorkflow Compile(WorkflowBlueprint blueprint)
    {
        ArgumentNullException.ThrowIfNull(blueprint);

        var errors = new List<string>();
        ValidateStructure(blueprint, errors);

        if (errors.Count > 0)
        {
            throw new WorkflowCompilationException(blueprint.Id, errors);
        }

        // First pass: hydrate every stage with an empty edge list so we can validate
        // transition targets against it (every target must be a known stage).
        var compiledStages = new Dictionary<string, CompiledStage>(StringComparer.Ordinal);

        foreach (var stage in blueprint.Stages)
        {
            var edges = new List<CompiledStageEdge>(stage.Transitions.Count);

            foreach (var transition in stage.Transitions)
            {
                if (!blueprint.Stages.Any(s => string.Equals(s.Id, transition.TargetStageId, StringComparison.Ordinal)))
                {
                    errors.Add($"Stage '{stage.Id}' transitions to unknown stage '{transition.TargetStageId}'.");
                    continue;
                }
                if (!string.IsNullOrEmpty(transition.OnBlockedStageId)
                    && !blueprint.Stages.Any(s => string.Equals(s.Id, transition.OnBlockedStageId, StringComparison.Ordinal)))
                {
                    errors.Add($"Stage '{stage.Id}' transition to '{transition.TargetStageId}' has unknown onBlocked '{transition.OnBlockedStageId}'.");
                    continue;
                }

                EdgePredicate predicate;
                try
                {
                    predicate = BuildPredicateForTransition(transition);
                }
                catch (Exception ex)
                {
                    errors.Add($"Stage '{stage.Id}' transition to '{transition.TargetStageId}': {ex.Message}");
                    continue;
                }

                edges.Add(new CompiledStageEdge(transition, predicate, transition.OnBlockedStageId));
            }

            compiledStages[stage.Id] = new CompiledStage(stage, edges);
        }

        if (errors.Count > 0)
        {
            throw new WorkflowCompilationException(blueprint.Id, errors);
        }

        // Preserve blueprint ordering.
        var orderedStages = blueprint.Stages.Select(s => compiledStages[s.Id]).ToList();
        return new CompiledCallWorkflow(blueprint, orderedStages);
    }

    private static void ValidateStructure(WorkflowBlueprint blueprint, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(blueprint.Id))
        {
            errors.Add("Workflow id is required.");
        }
        if (blueprint.Stages.Count == 0)
        {
            errors.Add("Workflow must define at least one stage.");
        }
        if (string.IsNullOrWhiteSpace(blueprint.InitialStageId))
        {
            errors.Add("Workflow must declare an initialStageId.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stage in blueprint.Stages)
        {
            if (string.IsNullOrWhiteSpace(stage.Id))
            {
                errors.Add("A stage with an empty id is not allowed.");
                continue;
            }
            if (!seen.Add(stage.Id))
            {
                errors.Add($"Duplicate stage id '{stage.Id}'.");
            }
        }

        if (!string.IsNullOrWhiteSpace(blueprint.InitialStageId)
            && !seen.Contains(blueprint.InitialStageId))
        {
            errors.Add($"InitialStageId '{blueprint.InitialStageId}' is not declared in stages.");
        }
    }

    private EdgePredicate BuildPredicateForTransition(TransitionBlueprint transition)
    {
        if (transition.Requires.Count == 0)
        {
            return BuiltInPredicates.Always();
        }

        var predicates = new EdgePredicate[transition.Requires.Count];
        for (var i = 0; i < transition.Requires.Count; i++)
        {
            predicates[i] = BuildPredicate(transition.Requires[i]);
        }
        return predicates.Length == 1 ? predicates[0] : BuiltInPredicates.All(predicates);
    }

    private EdgePredicate BuildPredicate(PredicateRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.Kind switch
        {
            PredicateKind.AuthLevel => reference.AuthLevel is { } level
                ? BuiltInPredicates.AuthVerificationLevel(level, reference.FailureMessage)
                : throw new ArgumentException("AuthLevel predicate requires AuthLevel to be set."),

            PredicateKind.StateHas => !string.IsNullOrEmpty(reference.Key)
                ? BuiltInPredicates.StateHas(reference.Key, reference.FailureMessage)
                : throw new ArgumentException("StateHas predicate requires Key to be set."),

            PredicateKind.StateEquals => !string.IsNullOrEmpty(reference.Key)
                ? BuiltInPredicates.StateEquals(reference.Key, reference.ExpectedValue, reference.FailureMessage)
                : throw new ArgumentException("StateEquals predicate requires Key to be set."),

            PredicateKind.Named when !string.IsNullOrEmpty(reference.NamedId) =>
                _namedPredicates?.TryResolve(reference.NamedId)
                    ?? throw new InvalidOperationException(
                        _namedPredicates is null
                            ? $"Named predicate '{reference.NamedId}' requested but no INamedEdgePredicateProvider was supplied to the compiler."
                            : $"Named predicate '{reference.NamedId}' is not registered."),

            PredicateKind.Named => throw new ArgumentException("Named predicate requires NamedId to be set."),

            _ => throw new ArgumentOutOfRangeException(nameof(reference.Kind), reference.Kind, "Unknown predicate kind."),
        };
    }
}
