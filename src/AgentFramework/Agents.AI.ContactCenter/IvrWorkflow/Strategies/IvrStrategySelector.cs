using System.Collections.Generic;
using System.Linq;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.IvrWorkflow.Strategies;

/// <inheritdoc cref="IIvrStrategySelector"/>
public sealed class IvrStrategySelector : IIvrStrategySelector
{
    private readonly AgentTierOptions _tierOptions;

    public IvrStrategySelector(IOptions<AgentTierOptions> tierOptions)
    {
        ArgumentNullException.ThrowIfNull(tierOptions);
        _tierOptions = tierOptions.Value;
    }

    public AgentTier SelectInitialTier(IvrStrategyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        foreach (var tier in EnumerateTiers(policy))
        {
            if (IsTierAvailable(tier))
            {
                return tier;
            }
        }

        // Last resort: DTMF is always available.
        return AgentTier.DtmfOnly;
    }

    public AgentTier? SelectFallbackTier(IvrStrategyPolicy policy, AgentTier current)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.AllowMidCallDegradation)
        {
            return null;
        }

        var ordered = EnumerateTiers(policy).ToList();
        var idx = ordered.IndexOf(current);
        if (idx < 0)
        {
            return null;
        }

        for (var i = idx + 1; i < ordered.Count; i++)
        {
            if (IsTierAvailable(ordered[i]))
            {
                return ordered[i];
            }
        }
        return null;
    }

    public IReadOnlyList<AgentTier> ResolvePrewarmTiers(IvrStrategyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.PrewarmTiers.Count == 0)
        {
            return [];
        }
        return policy.PrewarmTiers
            .Select(m => m.ToTier())
            .Where(IsTierAvailable)
            .Distinct()
            .ToList();
    }

    private bool IsTierAvailable(AgentTier tier)
    {
        if (!_tierOptions.Tiers.TryGetValue(tier, out var config))
        {
            return true;
        }
        return config.Enabled;
    }

    private static IEnumerable<AgentTier> EnumerateTiers(IvrStrategyPolicy policy)
    {
        if (policy.Primary == IvrInteractionMode.Mixed)
        {
            yield return AgentTier.RealtimeVoice;
        }
        else
        {
            yield return policy.Primary.ToTier();
        }
        foreach (var f in policy.Fallback)
        {
            yield return f.ToTier();
        }
    }
}
