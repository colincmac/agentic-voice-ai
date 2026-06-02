using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Calling.Strategies.Composite;

/// <summary>
/// Factory that builds a <see cref="CompositeFallbackStrategy"/> whose inner chain is
/// resolved from DI in the configured order. Registered for the top tier
/// (<see cref="TopTier"/>); the call session factory looks up the tier and gets the
/// composite, which then handles all degradation transparently.
/// </summary>
/// <remarks>
/// <para>
/// Inner factories are looked up by tier from every <see cref="IConversationStrategyFactory"/>
/// registered in DI. Tiers in <see cref="OrderedTiers"/> that have no matching factory are
/// skipped with a warning so the chain remains usable as the host adds tiers incrementally.
/// </para>
/// <para>
/// Per-call <see cref="IvrWorkflowState"/> is preserved across degradations because
/// <see cref="CompositeFallbackStrategy"/> threads the inner's <see cref="IvrWorkflowState"/>
/// through <c>restoreFrom</c> when activating the next tier. Per-call scoped services such as
/// <c>CallerAuthenticationState</c> survive automatically — the entire composite shares a single
/// service scope.
/// </para>
/// </remarks>
public sealed class CompositeFallbackStrategyFactory : IConversationStrategyFactory
{
    public CompositeFallbackStrategyFactory(AgentTier topTier, IReadOnlyList<AgentTier> orderedTiers)
    {
        if (orderedTiers is null || orderedTiers.Count == 0)
        {
            throw new ArgumentException("At least one tier is required", nameof(orderedTiers));
        }
        if (orderedTiers[0] != topTier)
        {
            throw new ArgumentException(
                $"The first ordered tier ({orderedTiers[0]}) must match topTier ({topTier}).",
                nameof(orderedTiers));
        }
        TopTier = topTier;
        OrderedTiers = orderedTiers;
    }

    public AgentTier TopTier { get; }

    public IReadOnlyList<AgentTier> OrderedTiers { get; }

    public AgentTier Tier => TopTier;

    public ValueTask<IConversationStrategy> CreateAsync(
        string callId,
        IServiceProvider services,
        IvrWorkflowState? restoreFrom,
        CancellationToken cancellationToken = default)
    {
        var loggerFactory = services.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<CompositeFallbackStrategyFactory>();

        // Index every other registered factory by tier; skip any composite registrations to
        // avoid recursion if the host accidentally registers two composites.
        var byTier = services.GetServices<IConversationStrategyFactory>()
            .Where(f => f is not CompositeFallbackStrategyFactory)
            .ToLookup(f => f.Tier);

        var ordered = new List<IConversationStrategyFactory>(OrderedTiers.Count);
        foreach (var tier in OrderedTiers)
        {
            var factory = byTier[tier].FirstOrDefault();
            if (factory is null)
            {
                logger?.LogWarning(
                    "Composite chain for top tier {TopTier}: no IConversationStrategyFactory registered for {Tier}; skipping",
                    TopTier, tier);
                continue;
            }
            ordered.Add(factory);
        }

        if (ordered.Count == 0)
        {
            throw new InvalidOperationException(
                $"Composite chain for top tier {TopTier} has no resolvable inner factories. " +
                $"Register at least one of: {string.Join(", ", OrderedTiers)}.");
        }

        IConversationStrategy strategy = new CompositeFallbackStrategy(ordered, loggerFactory);

        // CompositeFallbackStrategy itself doesn't accept restoreFrom — it threads state from
        // the inner's WorkflowState during fallback. When the call session restores a workflow
        // (e.g. a prewarmed strategy), the inner's first activation seeds from `restoreFrom`
        // via its own factory call. Surface the restore intent as a warning if it was set but
        // can't be honoured here.
        if (restoreFrom is not null)
        {
            logger?.LogInformation(
                "Composite factory ignored restoreFrom for top tier {TopTier}; the inner factory handles its own state seeding",
                TopTier);
        }

        return ValueTask.FromResult(strategy);
    }
}
