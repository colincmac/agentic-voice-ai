using System.Collections.Generic;
using Agents.AI.ContactCenter.Configuration;

namespace Agents.AI.ContactCenter.IvrWorkflow.Strategies;

/// <summary>
/// Picks the runtime <see cref="AgentTier"/> a call should use, intersecting the
/// workflow/stage <see cref="IvrStrategyPolicy"/> with the host's
/// <see cref="AgentTierOptions"/> (capacity, enabled flags).
/// </summary>
public interface IIvrStrategySelector
{
    /// <summary>Selects the highest-priority tier currently allowed by host capacity.</summary>
    AgentTier SelectInitialTier(IvrStrategyPolicy policy);

    /// <summary>
    /// Selects the next fallback tier when <paramref name="current"/> is no longer
    /// viable. Returns <see langword="null"/> when no further fallback is possible.
    /// </summary>
    AgentTier? SelectFallbackTier(IvrStrategyPolicy policy, AgentTier current);

    /// <summary>The tiers the host should pre-warm given the policy.</summary>
    IReadOnlyList<AgentTier> ResolvePrewarmTiers(IvrStrategyPolicy policy);
}
