using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.IvrWorkflow.Registry;

/// <summary>
/// DI-registered registry that resolves tool names (declared in YAML) to executable
/// <see cref="AITool"/> instances. The registry supports three population paths:
/// <list type="number">
///   <item>Direct registration via <see cref="AddTool(AITool)"/> or <see cref="AddTool(string, AITool)"/>.</item>
///   <item>Reflection-based scanning via <see cref="AddFromAssembly(System.Reflection.Assembly, System.Text.Json.JsonSerializerOptions?)"/>
///         and <see cref="AddFromType(System.Type, object?, System.Text.Json.JsonSerializerOptions?)"/>, which discover
///         <c>[McpServerTool]</c>- and <c>[AITool]</c>-decorated methods.</item>
///   <item>Bulk attachment through <see cref="AddTools(IEnumerable{AITool})"/>.</item>
/// </list>
/// All overloads dedupe by <see cref="AITool.Name"/>; the last registration wins.
/// </summary>
public interface IIvrToolRegistry
{
    /// <summary>The tools currently registered.</summary>
    IReadOnlyCollection<AITool> Tools { get; }

    /// <summary>Resolve a tool by name. Returns <see langword="null"/> when no tool is registered under <paramref name="name"/>.</summary>
    AITool? Resolve(string name);

    /// <summary>Resolve a set of tool names, throwing when any are missing.</summary>
    IReadOnlyList<AITool> ResolveAll(IEnumerable<string> names);

    /// <summary>Register a tool, using its <see cref="AITool.Name"/> as the key.</summary>
    IIvrToolRegistry AddTool(AITool tool);

    /// <summary>Register a tool under an explicit name override.</summary>
    IIvrToolRegistry AddTool(string name, AITool tool);

    /// <summary>Register a sequence of tools by their <see cref="AITool.Name"/>.</summary>
    IIvrToolRegistry AddTools(IEnumerable<AITool> tools);

    /// <summary>
    /// Reflect over a type, instantiate it (or use <paramref name="instance"/>), and
    /// register every method decorated with <c>[McpServerTool]</c> or
    /// <c>[Microsoft.Extensions.AI.AITool]</c> (matched by attribute name to avoid
    /// hard-coding a MCP package reference).
    /// </summary>
    IIvrToolRegistry AddFromType(
        System.Type type,
        object? instance = null,
        System.Text.Json.JsonSerializerOptions? serializerOptions = null);

    /// <summary>Reflect over every public type in <paramref name="assembly"/> and call <see cref="AddFromType"/>.</summary>
    IIvrToolRegistry AddFromAssembly(
        System.Reflection.Assembly assembly,
        System.Text.Json.JsonSerializerOptions? serializerOptions = null);
}
