using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Predicates;
using Agents.AI.ContactCenter.IvrWorkflow.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agents.AI.ContactCenter.IvrWorkflow.DependencyInjection;

/// <summary>
/// DI extensions for the workflow framework. Replaces the larger surface in
/// <c>IvrWorkflowServiceCollectionExtensions</c> with a minimal set: register the compiler,
/// register one or more <see cref="WorkflowBlueprint"/> instances, build the catalog.
/// </summary>
/// <remarks>
/// Workflows are <em>compiled at host startup</em>. Authors register
/// blueprints (programmatically or via a future YAML loader); the catalog is materialized
/// once on first resolution. Tools resolve through the keyed
/// <see cref="IIvrToolRegistry"/> (registered with
/// <see cref="IvrToolServiceCollectionExtensions.AddIvrTool(IServiceCollection, string, string, Func{IServiceProvider, Microsoft.Extensions.AI.AIFunction}, ServiceLifetime)"/>);
/// named predicates resolve through <see cref="INamedEdgePredicateProvider"/>.
/// </remarks>
public static class CallWorkflowServiceCollectionExtensions
{
    /// <summary>
    /// Register the workflow compiler and a <see cref="ICallWorkflowCatalog"/> that
    /// materializes from every <see cref="WorkflowBlueprint"/> resolvable from the root
    /// scope. Idempotent. This overload does not wire an
    /// <see cref="IIvrToolRegistry"/>, so per-stage tool surfaces will be empty —
    /// intended for tests and greenfield scenarios that do not surface tools to the agent.
    /// Production hosts should call the
    /// <see cref="AddCallWorkflowFramework(IServiceCollection, string)"/> overload.
    /// </summary>
    public static IServiceCollection AddCallWorkflowFramework(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddNamedEdgePredicateProvider();

        services.TryAddSingleton<WorkflowGraphCompiler>();

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
    /// Register the workflow compiler bound to the <see cref="IIvrToolRegistry"/> keyed by
    /// <paramref name="agentKey"/>, and a <see cref="ICallWorkflowCatalog"/> that materializes
    /// every registered <see cref="WorkflowBlueprint"/> with compile-time tool-name validation.
    /// Idempotent.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="agentKey">
    /// DI service key shared with the realtime agent and the tool registry registered via
    /// <see cref="IvrToolServiceCollectionExtensions.AddIvrTool(IServiceCollection, string, string, Func{IServiceProvider, Microsoft.Extensions.AI.AIFunction}, ServiceLifetime)"/>.
    /// </param>
    public static IServiceCollection AddCallWorkflowFramework(this IServiceCollection services, string agentKey)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(agentKey);

        services.AddNamedEdgePredicateProvider();
        services.AddIvrToolRegistry(agentKey);

        // Replace any prior compiler registration so callers that mixed the two overloads
        // end up with a compiler that validates tool names.
        services.RemoveAll<WorkflowGraphCompiler>();
        services.AddSingleton(sp => new WorkflowGraphCompiler(
            sp.GetService<INamedEdgePredicateProvider>(),
            sp.GetRequiredKeyedService<IIvrToolRegistry>(agentKey)));

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
