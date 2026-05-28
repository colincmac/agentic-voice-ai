using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.IvrWorkflow.Registry;

/// <inheritdoc cref="IIvrToolRegistry"/>
public sealed class IvrToolRegistry : IIvrToolRegistry
{
    private readonly ConcurrentDictionary<string, AITool> _tools = new(StringComparer.Ordinal);
    private readonly IServiceProvider? _services;

    public IvrToolRegistry(IServiceProvider? services = null)
    {
        _services = services;
    }

    public IReadOnlyCollection<AITool> Tools => (IReadOnlyCollection<AITool>)_tools.Values;

    public AITool? Resolve(string name) =>
        _tools.TryGetValue(name, out var tool) ? tool : null;

    public IReadOnlyList<AITool> ResolveAll(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var resolved = new List<AITool>();
        var missing = new List<string>();
        foreach (var n in names)
        {
            if (_tools.TryGetValue(n, out var tool))
            {
                resolved.Add(tool);
            }
            else
            {
                missing.Add(n);
            }
        }
        if (missing.Count > 0)
        {
            throw new IvrToolResolutionException(missing);
        }
        return resolved;
    }

    public IIvrToolRegistry AddTool(AITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools[tool.Name] = tool;
        return this;
    }

    public IIvrToolRegistry AddTool(string name, AITool tool)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(tool);
        _tools[name] = tool;
        return this;
    }

    public IIvrToolRegistry AddTools(IEnumerable<AITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        foreach (var t in tools)
        {
            AddTool(t);
        }
        return this;
    }

    public IIvrToolRegistry AddFromType(
        Type type,
        object? instance = null,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.IsAbstract || type.IsInterface)
        {
            return this;
        }

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(IsToolMethod);

        object? target = null;
        foreach (var method in methods)
        {
            if (!method.IsStatic)
            {
                target ??= instance ?? CreateInstance(type);
            }

            var name = ResolveToolName(method);
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
            var function = method.IsStatic
                ? AIFunctionFactory.Create(method, target: null, name: name, description: description, serializerOptions: serializerOptions)
                : AIFunctionFactory.Create(method, target: target, name: name, description: description, serializerOptions: serializerOptions);
            _tools[name] = function;
        }

        return this;
    }

    public IIvrToolRegistry AddFromAssembly(Assembly assembly, JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var type in SafeGetTypes(assembly))
        {
            if (type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Any(IsToolMethod))
            {
                AddFromType(type, instance: null, serializerOptions);
            }
        }
        return this;
    }

    private static bool IsToolMethod(MethodInfo method)
    {
        foreach (var attr in method.GetCustomAttributes(inherit: true))
        {
            var attrName = attr.GetType().Name;
            if (attrName is "McpServerToolAttribute" or "AIToolAttribute" or "AIFunctionAttribute")
            {
                return true;
            }
        }
        return false;
    }

    private static string ResolveToolName(MethodInfo method)
    {
        // Prefer attribute-supplied name if exposed.
        foreach (var attr in method.GetCustomAttributes(inherit: true))
        {
            var nameProp = attr.GetType().GetProperty("Name");
            if (nameProp?.GetValue(attr) is string name && !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        return method.Name;
    }

    private object CreateInstance(Type type)
    {
        // Prefer DI activation when a service provider is available.
        if (_services is not null)
        {
            var fromDi = _services.GetService(type);
            if (fromDi is not null)
            {
                return fromDi;
            }

            try
            {
                return Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance(_services, type);
            }
            catch
            {
                // Fall through to default construction.
            }
        }

        return System.Activator.CreateInstance(type)
            ?? throw new InvalidOperationException(
                $"Cannot instantiate tool type '{type.FullName}'. Register the type in DI or expose a parameterless constructor.");
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}

/// <summary>Thrown by <see cref="IIvrToolRegistry.ResolveAll"/> when one or more tool names are missing.</summary>
public sealed class IvrToolResolutionException : Exception
{
    public IvrToolResolutionException(IReadOnlyList<string> missing)
        : base($"Unresolved IVR tool references: {string.Join(", ", missing)}")
    {
        MissingNames = missing;
    }

    public IReadOnlyList<string> MissingNames { get; }
}
