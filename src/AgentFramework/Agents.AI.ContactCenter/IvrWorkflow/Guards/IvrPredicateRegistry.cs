using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Agents.AI.ContactCenter.IvrWorkflow.Guards;

/// <inheritdoc cref="IIvrPredicateRegistry"/>
public sealed class IvrPredicateRegistry : IIvrPredicateRegistry
{
    private readonly ConcurrentDictionary<string, RegisteredPredicate> _predicates = new(StringComparer.Ordinal);

    public IIvrPredicateRegistry Add(string name, Func<IvrWorkflowState, bool> predicate, string? failureMessage = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(predicate);
        _predicates[name] = new RegisteredPredicate(
            failureMessage ?? $"Predicate '{name}' did not pass.",
            SyncPredicate: predicate,
            AsyncPredicate: null);
        return this;
    }

    public IIvrPredicateRegistry AddAsync(string name, Func<IvrWorkflowState, CancellationToken, Task<bool>> predicate, string? failureMessage = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(predicate);
        _predicates[name] = new RegisteredPredicate(
            failureMessage ?? $"Predicate '{name}' did not pass.",
            SyncPredicate: null,
            AsyncPredicate: predicate);
        return this;
    }

    public IIvrStepGuard Resolve(string name, string? failureMessageOverride = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!_predicates.TryGetValue(name, out var entry))
        {
            throw new InvalidOperationException(
                $"Predicate '{name}' is not registered. Add it via IIvrPredicateRegistry.Add(...) during host setup.");
        }

        var message = failureMessageOverride ?? entry.FailureMessage;
        return entry.AsyncPredicate is not null
            ? new AsyncPredicateGuard(entry.AsyncPredicate, message)
            : new PredicateGuard(entry.SyncPredicate!, message);
    }

    public bool Contains(string name) => _predicates.ContainsKey(name);

    private sealed record RegisteredPredicate(
        string FailureMessage,
        Func<IvrWorkflowState, bool>? SyncPredicate = null,
        Func<IvrWorkflowState, CancellationToken, Task<bool>>? AsyncPredicate = null);
}
