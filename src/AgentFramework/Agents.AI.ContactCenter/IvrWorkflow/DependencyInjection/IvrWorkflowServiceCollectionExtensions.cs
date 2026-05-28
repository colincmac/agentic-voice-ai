using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Guards;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;
using Agents.AI.ContactCenter.IvrWorkflow.Registry;
using Agents.AI.ContactCenter.IvrWorkflow.Strategies;
using Agents.AI.ContactCenter.IvrWorkflow.Workflows;
using Azure.Storage.Blobs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agents.AI.ContactCenter.IvrWorkflow.DependencyInjection;

/// <summary>
/// DI registration helpers for the declarative IVR workflow framework. A typical host
/// composes the framework like this:
/// <code>
/// services.AddIvrWorkflowFramework(builder =>
/// {
///     builder.AddFileSystemSource(Path.Combine(AppContext.BaseDirectory, "IvrWorkflow", "Samples"))
///            .AddConfigurationSource(configuration)
///            .AddToolsFromAssembly(typeof(MyBankingTools).Assembly)
///            .AddPredicate("isVip", state => state.Get&lt;bool&gt;("isVip"));
/// });
/// </code>
/// </summary>
public static class IvrWorkflowServiceCollectionExtensions
{
    /// <summary>Register the declarative IVR workflow framework and let the caller compose sources, tools, and guards.</summary>
    public static IServiceCollection AddIvrWorkflowFramework(
        this IServiceCollection services,
        Action<IvrWorkflowFrameworkBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IIvrPredicateRegistry, IvrPredicateRegistry>();
        services.TryAddSingleton<IIvrStrategySelector, IvrStrategySelector>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIvrGuardFactory, AuthGuardFactory>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIvrGuardFactory, StateGuardFactory>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIvrGuardFactory, PreviousStageGuardFactory>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIvrGuardFactory, PredicateGuardFactory>());

        var registrations = new IvrFrameworkRegistrations();
        services.TryAddSingleton(registrations);

        services.TryAddSingleton<IIvrToolRegistry>(sp =>
        {
            var regs = sp.GetRequiredService<IvrFrameworkRegistrations>();
            var registry = new IvrToolRegistry(sp);
            registry.AddBuiltIns();
            foreach (var tool in regs.Tools)
            {
                registry.AddTool(tool);
            }
            foreach (var (name, tool) in regs.NamedTools)
            {
                registry.AddTool(name, tool);
            }
            foreach (var (name, factory) in regs.ToolFactories)
            {
                var tool = factory(sp);
                if (name is null)
                {
                    registry.AddTool(tool);
                }
                else
                {
                    registry.AddTool(name, tool);
                }
            }
            foreach (var (assembly, json) in regs.ToolAssemblies)
            {
                registry.AddFromAssembly(assembly, json);
            }
            return registry;
        });

        services.TryAddSingleton<IIvrWorkflowCompiler>(sp =>
        {
            var regs = sp.GetRequiredService<IvrFrameworkRegistrations>();
            var predicates = sp.GetRequiredService<IIvrPredicateRegistry>();
            foreach (var p in regs.Predicates)
            {
                if (p.Async is not null)
                {
                    predicates.AddAsync(p.Name, p.Async, p.FailureMessage);
                }
                else if (p.Sync is not null)
                {
                    predicates.Add(p.Name, p.Sync, p.FailureMessage);
                }
            }
            return new IvrWorkflowCompiler(
                sp.GetRequiredService<IIvrToolRegistry>(),
                predicates,
                sp.GetServices<IIvrGuardFactory>(),
                // Phase 2: stage imports need a catalog to resolve other workflows by id.
                // Pass a deferred accessor so we don't create a DI cycle (Compiler →
                // Loader → Catalog → Compiler); the catalog is resolved lazily on first
                // use by the time the compiler is actually invoked.
                catalogAccessor: () => sp.GetRequiredService<IIvrWorkflowCatalog>());
        });

        services.TryAddSingleton<IIvrWorkflowLoader>(sp =>
        {
            var regs = sp.GetRequiredService<IvrFrameworkRegistrations>();
            var sources = regs.BuildSources(sp);
            var source = sources.Count switch
            {
                0 => throw new InvalidOperationException(
                    "No IVR workflow sources are registered. Call builder.AddFileSystemSource(...), AddConfigurationSource(...), AddBlobSource(...), or AddSource<T>()."),
                1 => sources[0],
                _ => new CompositeIvrWorkflowSource(sources),
            };
            return new IvrWorkflowLoader(source, sp.GetRequiredService<IIvrWorkflowCompiler>());
        });

        // Catalog: lazily compiles + caches workflows by id so the navigator can resolve
        // sub-workflow stages (Phase 1) and, later, version-pinned imports (Phase 2).
        services.TryAddSingleton<IIvrWorkflowCatalog>(sp =>
            new IvrWorkflowCatalog(sp.GetRequiredService<IIvrWorkflowLoader>()));

        services.TryAddSingleton<IIvrWorkflowGraphBuilder, IvrWorkflowGraphBuilder>();

        var builder = new IvrWorkflowFrameworkBuilder(services, registrations);
        configure?.Invoke(builder);
        return services;
    }
}

/// <summary>Fluent builder used transitively by <see cref="IvrWorkflowServiceCollectionExtensions.AddIvrWorkflowFramework"/>.</summary>
public sealed class IvrWorkflowFrameworkBuilder
{
    private readonly IvrFrameworkRegistrations _registrations;

    internal IvrWorkflowFrameworkBuilder(IServiceCollection services, IvrFrameworkRegistrations registrations)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        _registrations = registrations;
    }

    public IServiceCollection Services { get; }

    public IvrWorkflowFrameworkBuilder AddFileSystemSource(string rootDirectory, string? name = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        _registrations.SourceFactories.Add(_ => new FileSystemWorkflowSource(rootDirectory) { Name = name ?? "filesystem" });
        return this;
    }

    public IvrWorkflowFrameworkBuilder AddConfigurationSource(IConfiguration configuration, string sectionName = "IvrWorkflows")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _registrations.SourceFactories.Add(_ => new ConfigurationWorkflowSource(configuration, sectionName));
        return this;
    }

    public IvrWorkflowFrameworkBuilder AddBlobSource(BlobContainerClient container, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        _registrations.SourceFactories.Add(_ => new BlobStorageWorkflowSource(container) { Name = name ?? "azure-blob" });
        return this;
    }

    public IvrWorkflowFrameworkBuilder AddSource<TSource>() where TSource : class, IIvrWorkflowDefinitionSource
    {
        Services.TryAddSingleton<TSource>();
        _registrations.SourceFactories.Add(sp => sp.GetRequiredService<TSource>());
        return this;
    }

    public IvrWorkflowFrameworkBuilder AddTool(AITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _registrations.Tools.Add(tool);
        return this;
    }

    public IvrWorkflowFrameworkBuilder AddTool(string name, AITool tool)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(tool);
        _registrations.NamedTools.Add((name, tool));
        return this;
    }

    /// <summary>
    /// Register a tool produced from DI. The factory runs once when the
    /// <see cref="IIvrToolRegistry"/> singleton is materialized. Use this overload when the
    /// tool needs scoped or singleton services (e.g. a caller directory or logger factory).
    /// </summary>
    public IvrWorkflowFrameworkBuilder AddTool(Func<IServiceProvider, AITool> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _registrations.ToolFactories.Add((null, factory));
        return this;
    }

    /// <summary>Register a DI-produced tool under an explicit name override (matched against YAML <c>tool:</c> references).</summary>
    public IvrWorkflowFrameworkBuilder AddTool(string name, Func<IServiceProvider, AITool> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(factory);
        _registrations.ToolFactories.Add((name, factory));
        return this;
    }

    public IvrWorkflowFrameworkBuilder AddToolsFromAssembly(Assembly assembly, JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        _registrations.ToolAssemblies.Add((assembly, serializerOptions));
        return this;
    }

    public IvrWorkflowFrameworkBuilder AddPredicate(string name, Func<IvrWorkflowState, bool> predicate, string? failureMessage = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(predicate);
        _registrations.Predicates.Add(new PredicateEntry(name, predicate, null, failureMessage));
        return this;
    }

    public IvrWorkflowFrameworkBuilder AddPredicateAsync(
        string name,
        Func<IvrWorkflowState, CancellationToken, Task<bool>> predicate,
        string? failureMessage = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(predicate);
        _registrations.Predicates.Add(new PredicateEntry(name, null, predicate, failureMessage));
        return this;
    }
}

internal sealed class IvrFrameworkRegistrations
{
    public List<Func<IServiceProvider, IIvrWorkflowDefinitionSource>> SourceFactories { get; } = [];
    public List<AITool> Tools { get; } = [];
    public List<(string Name, AITool Tool)> NamedTools { get; } = [];
    public List<(string? Name, Func<IServiceProvider, AITool> Factory)> ToolFactories { get; } = [];
    public List<(Assembly Assembly, JsonSerializerOptions? Json)> ToolAssemblies { get; } = [];
    public List<PredicateEntry> Predicates { get; } = [];

    public List<IIvrWorkflowDefinitionSource> BuildSources(IServiceProvider services) =>
        SourceFactories.Select(f => f(services)).ToList();
}

internal sealed record PredicateEntry(
    string Name,
    Func<IvrWorkflowState, bool>? Sync,
    Func<IvrWorkflowState, CancellationToken, Task<bool>>? Async,
    string? FailureMessage);
