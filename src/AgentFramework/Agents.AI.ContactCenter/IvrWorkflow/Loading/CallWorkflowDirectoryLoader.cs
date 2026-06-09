using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.IvrWorkflow.Loading;

/// <summary>
/// File-system loader for the new <see cref="WorkflowBlueprint"/> YAML schema. Discovers
/// every <c>*.yaml</c> / <c>*.yml</c> file in <paramref name="rootDirectory"/> (recursively)
/// and parses each one through <see cref="CallWorkflowYamlReader"/>.
/// </summary>
public static class CallWorkflowDirectoryLoader
{
    private static readonly EnumerationOptions _enumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Load every YAML blueprint under <paramref name="rootDirectory"/>. Returns an empty list if the directory does not exist.</summary>
    public static IReadOnlyList<WorkflowBlueprint> Load(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        if (!Directory.Exists(rootDirectory))
        {
            return [];
        }

        var blueprints = new List<WorkflowBlueprint>();
        foreach (var path in EnumerateFiles(rootDirectory))
        {
            var yaml = File.ReadAllText(path);
            blueprints.Add(CallWorkflowYamlReader.Read(yaml, sourceName: path));
        }
        return blueprints;
    }

    /// <summary>
    /// Register every blueprint in <paramref name="rootDirectory"/> with the workflow
    /// framework (idempotently calling <see cref="CallWorkflowServiceCollectionExtensions.AddCallWorkflowFramework(IServiceCollection)"/>).
    /// </summary>
    public static IServiceCollection AddCallWorkflowsFromDirectory(
        this IServiceCollection services,
        string rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);

        services.AddCallWorkflowFramework();
        RegisterDiscoveredBlueprints(services, rootDirectory);
        return services;
    }

    /// <summary>
    /// Register every blueprint in <paramref name="rootDirectory"/> with the workflow
    /// framework wired to the <see cref="Tools.IIvrToolRegistry"/> keyed by
    /// <paramref name="agentKey"/>, so tool-name references in the YAML are validated at
    /// compile time. Calls
    /// <see cref="CallWorkflowServiceCollectionExtensions.AddCallWorkflowFramework(IServiceCollection, string)"/>
    /// idempotently.
    /// </summary>
    public static IServiceCollection AddCallWorkflowsFromDirectory(
        this IServiceCollection services,
        string rootDirectory,
        string agentKey)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        ArgumentException.ThrowIfNullOrEmpty(agentKey);

        services.AddCallWorkflowFramework(agentKey);
        RegisterDiscoveredBlueprints(services, rootDirectory);
        return services;
    }

    private static void RegisterDiscoveredBlueprints(IServiceCollection services, string rootDirectory)
    {
        foreach (var path in EnumerateFiles(rootDirectory))
        {
            services.AddSingleton<WorkflowBlueprint>(_ =>
                CallWorkflowYamlReader.Read(File.ReadAllText(path), sourceName: path));
        }
    }

    private static IEnumerable<string> EnumerateFiles(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            yield break;
        }
        foreach (var path in Directory.EnumerateFiles(rootDirectory, "*.yaml", _enumerationOptions)
            .Concat(Directory.EnumerateFiles(rootDirectory, "*.yml", _enumerationOptions))
            .OrderBy(p => p, StringComparer.Ordinal))
        {
            yield return path;
        }
    }
}
