using System.Collections.Generic;
using Agents.AI.ContactCenter.IvrWorkflow.Definition;

namespace Agents.AI.ContactCenter.IvrWorkflow.Guards;

/// <summary>
/// Plug-in factory for custom YAML guard kinds. Implementations advertise the
/// <see cref="Type"/> value they handle (e.g., <c>"fraudCheck"</c>) and produce a runtime
/// <see cref="IIvrStepGuard"/> for each declaration. Registered through DI alongside
/// built-in handlers.
/// </summary>
public interface IIvrGuardFactory
{
    /// <summary>The YAML <c>type</c> value this factory handles.</summary>
    string Type { get; }

    /// <summary>Materialize a guard for the supplied YAML declaration.</summary>
    IIvrStepGuard Create(IvrGuardDocument document, IIvrGuardBuildContext context);
}

/// <summary>Build-time context passed to <see cref="IIvrGuardFactory.Create"/>.</summary>
public interface IIvrGuardBuildContext
{
    /// <summary>Workflow id under compilation (for diagnostics).</summary>
    string WorkflowName { get; }

    /// <summary>Stage id this guard belongs to, or <see langword="null"/> for capability/global guards.</summary>
    string? StageId { get; }

    /// <summary>Named predicates registered via <see cref="IIvrPredicateRegistry"/>.</summary>
    IIvrPredicateRegistry Predicates { get; }
}

/// <summary>Registry for named predicates referenced from <c>predicate</c>-typed guards in YAML.</summary>
public interface IIvrPredicateRegistry
{
    /// <summary>Register a synchronous predicate under a stable name.</summary>
    IIvrPredicateRegistry Add(string name, Func<IvrWorkflowState, bool> predicate, string? failureMessage = null);

    /// <summary>Register an async predicate under a stable name.</summary>
    IIvrPredicateRegistry AddAsync(string name, Func<IvrWorkflowState, CancellationToken, Task<bool>> predicate, string? failureMessage = null);

    /// <summary>Look up a registered predicate, throwing when it is missing.</summary>
    IIvrStepGuard Resolve(string name, string? failureMessageOverride = null);

    /// <summary>True when <paramref name="name"/> is registered.</summary>
    bool Contains(string name);
}
