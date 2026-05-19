using System;
using System.Collections.Generic;
using System.Linq;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Microsoft.Agents.AI.Workflows;

namespace Agents.AI.ContactCenter.IvrWorkflow.Workflows;

/// <inheritdoc cref="IIvrWorkflowGraphBuilder"/>
public sealed class IvrWorkflowGraphBuilder : IIvrWorkflowGraphBuilder
{
    /// <inheritdoc/>
    public Workflow Build(CompiledIvrWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        if (workflow.Stages.Count == 0)
        {
            throw new IvrWorkflowGraphBuildException(workflow.Name, "Workflow has no stages.");
        }

        // Each stage becomes one IvrStageExecutor; we keep stage id -> executor for edge wiring.
        var executors = new Dictionary<string, IvrStageExecutor>(StringComparer.Ordinal);
        foreach (var stage in workflow.Stages)
        {
            if (executors.ContainsKey(stage.Id))
            {
                throw new IvrWorkflowGraphBuildException(workflow.Name, $"Duplicate stage id '{stage.Id}'.");
            }

            executors[stage.Id] = new IvrStageExecutor(stage);
        }

        var entry = executors[workflow.Stages[0].Id];

        var builder = new WorkflowBuilder(entry)
            .WithName(workflow.Name);

        if (!string.IsNullOrWhiteSpace(workflow.Description))
        {
            builder.WithDescription(workflow.Description);
        }

        var terminalExecutors = new List<ExecutorBinding>();

        foreach (var stage in workflow.Stages)
        {
            var source = executors[stage.Id];

            if (stage.Terminal)
            {
                terminalExecutors.Add(source);

                // Terminal stages still expose any explicit transitions for visualization parity.
            }

            // Aggregate transitions from every authoring surface the compiler supports:
            //   - ConversationState.Transitions (explicit YAML `transitions:` + intent `next_stage` + `on_exit`)
            //   - StepDtmfConfiguration.MenuOptions[d].NextStepId (DTMF menu choices)
            //   - StepDtmfConfiguration.OnValidNextStepId (DTMF digit-collection success)
            // Dedupe by target so a stage with multiple intents/digits pointing at the same
            // next stage gets one edge (the predicate covers any trigger) and we never call
            // AddEdge twice for the same (source, target) pair.
            var distinctTargets = CollectTransitionTargets(stage);

            if (distinctTargets.Count == 0)
            {
                continue;
            }

            foreach (var target in distinctTargets)
            {
                if (!executors.TryGetValue(target, out var targetExecutor))
                {
                    throw new IvrWorkflowGraphBuildException(
                        workflow.Name,
                        $"Stage '{stage.Id}' transitions to unknown stage '{target}'.");
                }

                var fromId = stage.Id;
                var toId = target;

                builder.AddEdge<IvrStageMessage>(
                    source,
                    targetExecutor,
                    condition: msg => msg is not null && ShouldRoute(msg, fromId, toId, distinctTargets));
            }
        }

        if (terminalExecutors.Count > 0)
        {
            builder.WithOutputFrom([.. terminalExecutors]);
        }

        return builder.Build();
    }

    /// <summary>
    /// Edge predicate: route the message if it just left <paramref name="fromStageId"/> and
    /// either (a) the message carries an explicit <see cref="IvrStageMessage.NextStageIdHint"/>
    /// matching <paramref name="toStageId"/>, or (b) no hint was provided and this is the
    /// only outgoing edge from the source (so the runtime can still single-step).
    /// </summary>
    private static bool ShouldRoute(
        IvrStageMessage message,
        string fromStageId,
        string toStageId,
        IReadOnlyList<string> allTargets)
    {
        if (!string.Equals(message.FromStageId, fromStageId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(message.NextStageIdHint))
        {
            return string.Equals(message.NextStageIdHint, toStageId, StringComparison.Ordinal);
        }

        // No hint — fall through only when there's a single outgoing edge, so we never
        // accidentally fan out to every connected stage.
        return allTargets.Count == 1;
    }

    /// <summary>
    /// Collects every distinct next-stage id this stage can transition to, considering
    /// realtime/NLU transitions on <see cref="ConversationState"/> as well as DTMF
    /// menu options and digit-collection success transitions on
    /// <see cref="StepDtmfConfiguration"/>.
    /// </summary>
    private static List<string> CollectTransitionTargets(CompiledIvrStage stage)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            if (seen.Add(candidate))
            {
                ordered.Add(candidate);
            }
        }

        if (stage.RuntimeStep.ConversationState.Transitions is { } transitions)
        {
            foreach (var t in transitions)
            {
                Add(t.NextStep);
            }
        }

        if (stage.RuntimeStep.StepDtmfConfiguration is { } dtmf)
        {
            if (dtmf.MenuOptions is { Count: > 0 } options)
            {
                foreach (var option in options.Values)
                {
                    Add(option.NextStepId);
                }
            }

            Add(dtmf.OnValidNextStepId);
        }

        return ordered;
    }
}
