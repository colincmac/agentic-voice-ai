using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Navigation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.IvrWorkflow.Execution;

/// <summary>
/// Per-call bundle that wires the new <see cref="CompiledCallWorkflow"/> model to a
/// concrete strategy. 
/// </summary>
/// <remarks>
/// One <see cref="CallWorkflowSession"/> per call. The session owns the navigator + state
/// + a reference to the call's service scope so executors can resolve tools and predicates
/// without threading the provider through every API call.
/// </remarks>
public sealed class CallWorkflowSession
{
    private readonly Dictionary<CompiledStage, IReadOnlyList<AITool>> _stageToolCache = new(ReferenceEqualityComparer.Instance);
    private readonly Lock _toolCacheGate = new();

    public CallWorkflowSession(
        CompiledCallWorkflow workflow,
        IServiceProvider serviceProvider,
        IvrWorkflowState workflowState,
        CallerAuthenticationState authenticationState,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        Workflow = workflow;
        State = workflowState;
        CallerAuthenticationState = authenticationState;
        Services = serviceProvider;

        Navigator = new CallWorkflowNavigator(
            workflow,
            workflowState,
            authenticationState,
            loggerFactory?.CreateLogger<CallWorkflowNavigator>());
    }

    public IServiceProvider Services { get; }

    public CallerAuthenticationState CallerAuthenticationState { get; }


    /// <summary>Workflow being walked for this call.</summary>
    public CompiledCallWorkflow Workflow { get; }

    /// <summary>Per-call state shared with tools and observers. Survives tier swaps.</summary>
    public IvrWorkflowState State { get; }

    /// <summary>Navigator that owns the "where are we in the graph" question.</summary>
    public ICallWorkflowNavigator Navigator { get; }

    /// <summary>
    /// Materialize the tool surface for <paramref name="stage"/> against this call's
    /// service scope. The first call per <paramref name="stage"/> invokes every
    /// <see cref="Tools.ToolBinding.Factory"/> and caches the resulting list; subsequent
    /// calls return the same reference. The cache is keyed by <see cref="CompiledStage"/>
    /// reference, so the same identical <see cref="CompiledStage"/> re-entered
    /// (e.g. by an <c>onBlocked</c> bounce) does not re-materialize. Cache lives only as
    /// long as this session — a tier swap creates a new session and therefore re-materializes.
    /// </summary>
    public IReadOnlyList<AITool> GetToolsFor(CompiledStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);

        lock (_toolCacheGate)
        {
            if (_stageToolCache.TryGetValue(stage, out var cached))
            {
                return cached;
            }

            if (stage.ToolBindings.Count == 0)
            {
                return _stageToolCache[stage] = [];
            }

            var resolved = new List<AITool>(stage.ToolBindings.Count);
            foreach (var binding in stage.ToolBindings)
            {
                resolved.Add(binding.Factory(Services));
            }

            var snapshot = resolved.AsReadOnly();
            _stageToolCache[stage] = snapshot;
            return snapshot;
        }
    }
}

/// <summary>Factory abstraction so the call-session container can build per-call sessions through DI.</summary>
public interface ICallWorkflowSessionFactory
{
    /// <summary>Create a session for <paramref name="workflow"/>. <paramref name="restoreFrom"/> reuses prior state across tier swaps.</summary>
    CallWorkflowSession Create(
        CompiledCallWorkflow workflow,
        IServiceProvider services);
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
        IServiceProvider services)
    {
        var workflowState = services.GetService<IvrWorkflowState>() ?? new IvrWorkflowState();
        var authState = services.GetService<CallerAuthenticationState>() ?? new CallerAuthenticationState();

        return new CallWorkflowSession(workflow, services, workflowState, authState, _loggerFactory);
    }
}
