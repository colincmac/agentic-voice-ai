using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Navigation;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.IvrWorkflow.Execution;

/// <summary>
/// Per-call bundle that wires the new <see cref="CompiledCallWorkflow"/> model to a
/// concrete strategy. Replaces the legacy <c>IvrWorkflowSession</c> for callers that have
/// migrated to <see cref="Blueprint.WorkflowBlueprint"/>.
/// </summary>
/// <remarks>
/// One <see cref="CallWorkflowSession"/> per call. The session owns the navigator + state
/// + a reference to the call's service scope so executors can resolve tools and predicates
/// without threading the provider through every API call.
/// </remarks>
public sealed class CallWorkflowSession
{
    public CallWorkflowSession(
        CompiledCallWorkflow workflow,
        IvrWorkflowState state,
        IServiceProvider services,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(services);

        Workflow = workflow;
        State = state;
        Services = services;
        Navigator = new CallWorkflowNavigator(
            workflow,
            state,
            services,
            loggerFactory?.CreateLogger<CallWorkflowNavigator>());
    }

    /// <summary>Workflow being walked for this call.</summary>
    public CompiledCallWorkflow Workflow { get; }

    /// <summary>Per-call state shared with tools and observers. Survives tier swaps.</summary>
    public IvrWorkflowState State { get; }

    /// <summary>Call-scoped DI provider (per <c>ICallSession</c> scope).</summary>
    public IServiceProvider Services { get; }

    /// <summary>Navigator that owns the "where are we in the graph" question.</summary>
    public ICallWorkflowNavigator Navigator { get; }
}

/// <summary>Factory abstraction so the call-session container can build per-call sessions through DI.</summary>
public interface ICallWorkflowSessionFactory
{
    /// <summary>Create a session for <paramref name="workflow"/>. <paramref name="restoreFrom"/> reuses prior state across tier swaps.</summary>
    CallWorkflowSession Create(
        CompiledCallWorkflow workflow,
        IServiceProvider services,
        IvrWorkflowState? restoreFrom = null);
}

/// <summary>Default <see cref="ICallWorkflowSessionFactory"/>. Singleton; sessions are per-call.</summary>
public sealed class CallWorkflowSessionFactory : ICallWorkflowSessionFactory
{
    private readonly ILoggerFactory? _loggerFactory;

    public CallWorkflowSessionFactory(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
    }

    public CallWorkflowSession Create(
        CompiledCallWorkflow workflow,
        IServiceProvider services,
        IvrWorkflowState? restoreFrom = null)
    {
        var state = restoreFrom ?? new IvrWorkflowState();
        return new CallWorkflowSession(workflow, state, services, _loggerFactory);
    }
}
