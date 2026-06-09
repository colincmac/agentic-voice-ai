using System.Collections.Frozen;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.IvrWorkflow.Tools;

/// <summary>
/// Per-agent catalog of <see cref="AIFunction"/> bindings referenced by name from
/// <see cref="Blueprint.WorkflowBlueprint.CommonToolNames"/>,
/// <see cref="Blueprint.StageBlueprint.ToolNames"/>, and
/// <see cref="Blueprint.StageRealtimePrompt.ToolNames"/>.
/// </summary>
/// <remarks>
/// <para>
/// The registry is keyed by the same DI service key that resolves the agent
/// (e.g. <c>AgentConfig.TriageAgent</c>). This mirrors the Microsoft Agent
/// Framework convention where tools live alongside the keyed
/// <see cref="Microsoft.Agents.AI.AIAgent"/> they are intended for, without
/// requiring a one-agent-per-stage model: a single realtime agent is reused
/// across stages, with its prompt and tool surface re-projected on each
/// transition from the per-stage <see cref="ToolBinding"/> list.
/// </para>
/// <para>
/// Registrations are immutable once <see cref="IvrToolRegistry"/> is built;
/// the <see cref="WorkflowGraphCompiler"/> resolves every blueprint tool name
/// against the registry at host startup so missing names fail fast in
/// <see cref="WorkflowCompilationException"/> instead of mid-call.
/// </para>
/// </remarks>
public interface IIvrToolRegistry
{
    /// <summary>DI service key under which this registry was registered (matches the realtime agent's key).</summary>
    string AgentKey { get; }

    /// <summary>Returns the <see cref="ToolBinding"/> registered under <paramref name="name"/>, or <see langword="false"/> if no binding exists.</summary>
    bool TryGetBinding(string name, out ToolBinding binding);

    /// <summary>Every registered tool name. Diagnostic; ordering matches first-insertion order.</summary>
    IReadOnlyCollection<string> Names { get; }
}

/// <summary>
/// A single tool registration: the lookup <see cref="Name"/>, a DI
/// <see cref="Lifetime"/> hint for diagnostics, and a <see cref="Factory"/>
/// that produces the <see cref="AIFunction"/> from a per-call
/// <see cref="IServiceProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Lifetime"/> is descriptive: it documents how the registering
/// host expects the factory to behave (singleton functions reused across
/// calls vs. scoped functions that capture per-call state such as
/// <see cref="Authentication.CallerAuthenticationState"/>). The registry does
/// not cache results; the per-call cache lives on
/// <see cref="Execution.CallWorkflowSession"/>.
/// </para>
/// </remarks>
public readonly record struct ToolBinding(
    string Name,
    ServiceLifetime Lifetime,
    Func<IServiceProvider, AIFunction> Factory);

/// <summary>
/// Default immutable <see cref="IIvrToolRegistry"/>. Built once per agent key
/// by <see cref="IvrToolRegistryBuilder.Build"/>; backed by a
/// <see cref="FrozenDictionary{TKey,TValue}"/> for O(1) lookup.
/// </summary>
internal sealed class IvrToolRegistry : IIvrToolRegistry
{
    private readonly FrozenDictionary<string, ToolBinding> _bindings;

    public IvrToolRegistry(string agentKey, IReadOnlyList<ToolBinding> bindings)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentKey);
        ArgumentNullException.ThrowIfNull(bindings);

        AgentKey = agentKey;
        _bindings = bindings.ToFrozenDictionary(b => b.Name, StringComparer.Ordinal);
    }

    public string AgentKey { get; }

    public bool TryGetBinding(string name, out ToolBinding binding) =>
        _bindings.TryGetValue(name, out binding);

    public IReadOnlyCollection<string> Names => _bindings.Keys;
}

/// <summary>
/// Mutable per-agent-key collector that
/// <see cref="IvrToolServiceCollectionExtensions.AddIvrTool(IServiceCollection, string, string, Func{IServiceProvider, AIFunction}, ServiceLifetime)"/>
/// appends to during DI configuration. Registered as a keyed singleton; the
/// <see cref="IvrToolRegistry"/> snapshot is built lazily on first
/// <see cref="IIvrToolRegistry"/> resolution.
/// </summary>
/// <remarks>
/// Re-registering an existing <see cref="ToolBinding.Name"/> overwrites the
/// prior entry (last-wins), preserving the semantics of the legacy
/// <c>AddNamedAIFunction</c> helper.
/// </remarks>
internal sealed class IvrToolRegistryBuilder
{
    private readonly Dictionary<string, ToolBinding> _bindings = new(StringComparer.Ordinal);
    private readonly List<string> _orderedNames = [];
    private readonly Lock _gate = new();

    public IvrToolRegistryBuilder(string agentKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentKey);
        AgentKey = agentKey;
    }

    public string AgentKey { get; }

    /// <summary>Register or replace a binding. Last-wins on duplicate <paramref name="binding"/>.<see cref="ToolBinding.Name"/>.</summary>
    public void Add(ToolBinding binding)
    {
        ArgumentException.ThrowIfNullOrEmpty(binding.Name);
        ArgumentNullException.ThrowIfNull(binding.Factory);

        lock (_gate)
        {
            if (!_bindings.ContainsKey(binding.Name))
            {
                _orderedNames.Add(binding.Name);
            }
            _bindings[binding.Name] = binding;
        }
    }

    /// <summary>Materialize the immutable <see cref="IvrToolRegistry"/> snapshot.</summary>
    public IvrToolRegistry Build()
    {
        lock (_gate)
        {
            var ordered = new List<ToolBinding>(_orderedNames.Count);
            foreach (var name in _orderedNames)
            {
                ordered.Add(_bindings[name]);
            }
            return new IvrToolRegistry(AgentKey, ordered);
        }
    }
}
