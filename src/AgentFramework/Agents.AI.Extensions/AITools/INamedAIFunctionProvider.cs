using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.AITools;

/// <summary>
/// Resolves <see cref="AIFunction"/> instances by name from the current DI scope. The
/// provider unifies two registration paths: keyed <see cref="AIFunction"/> services
/// added via <see cref="NamedAIFunctionExtensions.AddNamedAIFunction(Microsoft.Extensions.DependencyInjection.IServiceCollection, string, System.Func{System.IServiceProvider, AIFunction}, Microsoft.Extensions.DependencyInjection.ServiceLifetime)"/>
/// and <see cref="AIFunction"/>-typed tools surfaced by <see cref="IAIToolCollection"/>
/// implementations registered in the same scope.
/// </summary>
public interface INamedAIFunctionProvider
{
    /// <summary>Returns the <see cref="AIFunction"/> registered under <paramref name="name"/>, or <see langword="null"/> if none is registered.</summary>
    AIFunction? TryResolve(string name);

    /// <summary>Returns the <see cref="AIFunction"/> registered under <paramref name="name"/> or throws <see cref="System.Collections.Generic.KeyNotFoundException"/>.</summary>
    AIFunction Resolve(string name);

    /// <summary>
    /// Resolves every name in <paramref name="names"/>, preserving order. Throws an
    /// aggregate <see cref="System.Collections.Generic.KeyNotFoundException"/> if any
    /// name fails to resolve so callers (e.g. workflow compilation) can fail fast.
    /// </summary>
    IReadOnlyList<AIFunction> ResolveAll(IEnumerable<string> names);

    /// <summary>Enumerates every distinct registered function name visible to this scope.</summary>
    IEnumerable<string> Names { get; }
}
