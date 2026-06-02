using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Predicates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agents.AI.ContactCenter.IvrWorkflow.DependencyInjection;

/// <summary>
/// DI extensions for the Phase 3 workflow framework. Replaces the larger surface in
/// <c>IvrWorkflowServiceCollectionExtensions</c> with a minimal set: register the compiler,
/// register one or more <see cref="WorkflowBlueprint"/> instances, build the catalog.
/// </summary>
/// <remarks>
/// Per the refactor plan, workflows are <em>compiled at host startup</em>. Authors register
/// blueprints (programmatically or via a future YAML loader); the catalog is materialized
/// once on first resolution. Tools and named predicates are resolved through the keyed-DI
/// providers from Phase 1/2 (<see cref="Extensions.AITools.INamedAIFunctionProvider"/> and
/// <see cref="INamedEdgePredicateProvider"/>).
/// </remarks>
public static class CallWorkflowServiceCollectionExtensions
{
    /// <summary>
    /// Register the workflow compiler and a <see cref="ICallWorkflowCatalog"/> that
    /// materializes from every <see cref="WorkflowBlueprint"/> resolvable from the root
    /// scope. Idempotent.
    /// </summary>
    public static IServiceCollection AddCallWorkflowFramework(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddNamedEdgePredicateProvider();

        services.TryAddSingleton<WorkflowGraphCompiler>(sp =>
            new WorkflowGraphCompiler(sp.GetService<INamedEdgePredicateProvider>()));

        services.TryAddSingleton<ICallWorkflowCatalog>(sp =>
        {
            var compiler = sp.GetRequiredService<WorkflowGraphCompiler>();
            var blueprints = sp.GetServices<WorkflowBlueprint>();
            var compiled = blueprints.Select(compiler.Compile);
            return new CallWorkflowCatalog(compiled);
        });

        return services;
    }

    /// <summary>
    /// Register a <see cref="WorkflowBlueprint"/> with the framework. The blueprint is
    /// compiled into the catalog at first resolution. Multiple calls add additional
    /// workflows; their ids must be unique.
    /// </summary>
    public static IServiceCollection AddCallWorkflow(
        this IServiceCollection services,
        WorkflowBlueprint blueprint)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(blueprint);

        services.AddCallWorkflowFramework();
        services.AddSingleton(blueprint);
        return services;
    }

    /// <summary>Factory overload of <see cref="AddCallWorkflow(IServiceCollection, WorkflowBlueprint)"/>.</summary>
    public static IServiceCollection AddCallWorkflow(
        this IServiceCollection services,
        Func<IServiceProvider, WorkflowBlueprint> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);

        services.AddCallWorkflowFramework();
        services.AddSingleton(factory);
        return services;
    }
}
