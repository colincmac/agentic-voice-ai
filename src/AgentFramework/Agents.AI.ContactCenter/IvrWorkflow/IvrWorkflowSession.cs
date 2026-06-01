using System.Diagnostics.CodeAnalysis;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// Per-call IVR collaborator bundle. Holds the navigator, its mutable state, the
/// compiled workflow definition, the catalog used for subflow resolution, and a
/// lazily-created <see cref="IvrAdvanceFunctions"/> set. Strategies receive a single
/// session instead of resolving each piece from <see cref="IServiceProvider"/>.
/// </summary>
/// <remarks>
/// Built by <see cref="IIvrWorkflowSessionFactory"/> once per call. State is shared
/// across tier swaps in a <see cref="Calling.Strategies.Composite.CompositeFallbackStrategy"/>
/// (passed in via <c>restoreFrom</c>); a fresh session — but the same state — is created
/// for the new tier.
/// </remarks>
public sealed class IvrWorkflowSession
{
    private readonly object _invokerLock = new();
    private readonly ILoggerFactory? _loggerFactory;
    private IvrAdvanceFunctions? _advanceFunctions;

    internal IvrWorkflowSession(
        RealtimeIvrWorkflowDefinition definition,
        IvrWorkflowState state,
        IIvrWorkflowNavigator navigator,
        IIvrWorkflowCatalog catalog,
        ILoggerFactory? loggerFactory)
    {
        Definition = definition;
        State = state;
        Navigator = navigator;
        Catalog = catalog;
        _loggerFactory = loggerFactory;
    }

    /// <summary>The root workflow definition the navigator was created for.</summary>
    public RealtimeIvrWorkflowDefinition Definition { get; }

    /// <summary>The mutable per-call workflow state. Survives tier swaps.</summary>
    public IvrWorkflowState State { get; }

    /// <summary>Navigator owning the state machine, transitions, subflow push/pop, and prompt rendering.</summary>
    public IIvrWorkflowNavigator Navigator { get; }

    /// <summary>Catalog used to resolve sub-workflow ids requested by the navigator.</summary>
    public IIvrWorkflowCatalog Catalog { get; }

    /// <summary>
    /// Lazily create (or return) the advance-function builder bound to
    /// <paramref name="applyStageAsync"/>. The first caller wins; subsequent calls return
    /// the same instance regardless of the callback they passed in. Strategies that
    /// expose IVR navigation as <c>advance_to_*</c> tools call this once during start.
    /// </summary>
    public IvrAdvanceFunctions GetOrCreateAdvanceFunctions(
        Func<RealtimeIvrWorkflowStep, CancellationToken, Task> applyStageAsync)
    {
        ArgumentNullException.ThrowIfNull(applyStageAsync);

        if (_advanceFunctions is not null)
        {
            return _advanceFunctions;
        }

        lock (_invokerLock)
        {
            _advanceFunctions ??= new IvrAdvanceFunctions(
                Navigator,
                applyStageAsync,
                _loggerFactory?.CreateLogger<IvrAdvanceFunctions>());
            return _advanceFunctions;
        }
    }

    /// <summary>
    /// Mark the workflow complete. Equivalent to <see cref="IIvrWorkflowNavigator.Complete"/>;
    /// strategies should call this rather than reaching through the navigator so the
    /// completion path stays in one place.
    /// </summary>
    public void Complete(IvrWorkflowStatus status = IvrWorkflowStatus.Completed) =>
        Navigator.Complete(status);

    /// <summary>
    /// Convenience helper for tests and ad-hoc hosts: build a session over
    /// <paramref name="definition"/> using whatever <see cref="IIvrWorkflowCatalog"/>
    /// is registered in <paramref name="services"/> (or an
    /// <see cref="EmptyIvrWorkflowCatalog"/> when nothing is registered).
    /// </summary>
    public static IvrWorkflowSession Create(
        RealtimeIvrWorkflowDefinition definition,
        IServiceProvider services,
        IvrWorkflowState? state = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(services);

        var catalog = services.GetService<IIvrWorkflowCatalog>() ?? new EmptyIvrWorkflowCatalog();
        var loggerFactory = services.GetService<ILoggerFactory>();
        var resolvedState = state ?? new IvrWorkflowState { Status = IvrWorkflowStatus.Running };

        var navigator = new IvrWorkflowNavigator(
            definition,
            resolvedState,
            services,
            catalog,
            loggerFactory?.CreateLogger<IvrWorkflowNavigator>());

        return new IvrWorkflowSession(definition, resolvedState, navigator, catalog, loggerFactory);
    }
}

/// <summary>
/// Builds an <see cref="IvrWorkflowSession"/> for a single call. Registered as a
/// singleton in DI; strategies request one from their factory's
/// <see cref="IServiceProvider"/>.
/// </summary>
public interface IIvrWorkflowSessionFactory
{
    /// <summary>
    /// Build a fresh session over <paramref name="definition"/>. Reuses
    /// <paramref name="restoreFrom"/> as the mutable state when supplied (tier swap),
    /// otherwise allocates a fresh state seeded with <see cref="IvrWorkflowStatus.Running"/>.
    /// </summary>
    IvrWorkflowSession Create(
        RealtimeIvrWorkflowDefinition definition,
        IvrWorkflowState? restoreFrom,
        IServiceProvider services);
}

/// <summary>
/// Default <see cref="IIvrWorkflowSessionFactory"/>. Resolves <see cref="IIvrWorkflowCatalog"/>
/// and an <see cref="ILoggerFactory"/> from the call-scoped <see cref="IServiceProvider"/>,
/// constructs an <see cref="IvrWorkflowNavigator"/>, and wraps everything in an
/// <see cref="IvrWorkflowSession"/>.
/// </summary>
public sealed class IvrWorkflowSessionFactory : IIvrWorkflowSessionFactory
{
    public IvrWorkflowSession Create(
        RealtimeIvrWorkflowDefinition definition,
        IvrWorkflowState? restoreFrom,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(services);

        return IvrWorkflowSession.Create(definition, services, restoreFrom);
    }
}

/// <summary>
/// Empty <see cref="IIvrWorkflowCatalog"/> used as the default when the host hasn't
/// registered a real catalog (single-workflow hosts and tests). Any subflow lookup
/// fails loudly with a clear message.
/// </summary>
public sealed class EmptyIvrWorkflowCatalog : IIvrWorkflowCatalog
{
    public IReadOnlyCollection<string> Ids => Array.Empty<string>();

    public IReadOnlyCollection<int> VersionsFor(string workflowId) => Array.Empty<int>();

    public bool TryGet(string workflowId, [NotNullWhen(true)] out CompiledIvrWorkflow? workflow)
    {
        workflow = null;
        return false;
    }

    public bool TryGet(
        string workflowId,
        int? minVersion,
        int? maxVersion,
        [NotNullWhen(true)] out CompiledIvrWorkflow? workflow)
    {
        workflow = null;
        return false;
    }

    public CompiledIvrWorkflow Get(string workflowId) => Get(workflowId, null, null);

    public CompiledIvrWorkflow Get(string workflowId, int? minVersion, int? maxVersion) =>
        throw new KeyNotFoundException(
            $"No IIvrWorkflowCatalog has been registered; cannot resolve workflow '{workflowId}'. " +
            "Register one via AddIvrWorkflowFramework(...) or supply a custom IIvrWorkflowCatalog.");

    public ValueTask EnsureLoadedAsync(CancellationToken cancellationToken = default) => default;
}
