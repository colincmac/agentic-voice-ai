using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.Extensions.AITools;

/// <summary>
/// Default <see cref="INamedAIFunctionProvider"/> implementation. Resolves names by
/// looking up keyed <see cref="AIFunction"/> services first, then falling back to any
/// <see cref="AIFunction"/>-typed tool surfaced by an <see cref="IAIToolCollection"/>
/// in the current scope. Registered as a singleton; per-call instances of scoped
/// functions are still produced because the provider always resolves through the
/// caller's <see cref="IServiceProvider"/> on every lookup.
/// </summary>
internal sealed class NamedAIFunctionProvider : INamedAIFunctionProvider
{
    private readonly IServiceProvider _services;
    private readonly ILogger<NamedAIFunctionProvider> _logger;

    public NamedAIFunctionProvider(
        IServiceProvider services,
        ILogger<NamedAIFunctionProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _logger = logger ?? NullLogger<NamedAIFunctionProvider>.Instance;
    }

    public AIFunction? TryResolve(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (_services.GetKeyedService<AIFunction>(name) is { } keyed)
        {
            return keyed;
        }

        foreach (var collection in _services.GetServices<IAIToolCollection>())
        {
            foreach (var tool in collection.AsAITools())
            {
                if (tool is AIFunction fn && string.Equals(fn.Name, name, StringComparison.Ordinal))
                {
                    return fn;
                }
            }
        }

        return null;
    }

    public AIFunction Resolve(string name) =>
        TryResolve(name) ?? throw new KeyNotFoundException(
            $"No AIFunction is registered under the name '{name}'. " +
            "Register one with services.AddNamedAIFunction(...) or via an IAIToolCollection.");

    public IReadOnlyList<AIFunction> ResolveAll(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var resolved = new List<AIFunction>();
        List<string>? missing = null;

        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var fn = TryResolve(name);
            if (fn is null)
            {
                (missing ??= []).Add(name);
                continue;
            }
            resolved.Add(fn);
        }

        if (missing is { Count: > 0 })
        {
            throw new KeyNotFoundException(
                $"The following AIFunction names could not be resolved: {string.Join(", ", missing)}. " +
                "Verify they were registered with services.AddNamedAIFunction(...) or surfaced by an IAIToolCollection.");
        }

        return resolved;
    }

    public IEnumerable<string> Names
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // We can't enumerate keyed registrations directly from IServiceProvider, so
            // we rely on the IAIToolCollection path for discovery. Keyed names are
            // still resolvable via Resolve/TryResolve; this enumeration is best-effort
            // and primarily intended for diagnostics.
            foreach (var collection in _services.GetServices<IAIToolCollection>())
            {
                foreach (var tool in collection.AsAITools())
                {
                    if (tool is AIFunction fn && seen.Add(fn.Name))
                    {
                        yield return fn.Name;
                    }
                }
            }
        }
    }
}
