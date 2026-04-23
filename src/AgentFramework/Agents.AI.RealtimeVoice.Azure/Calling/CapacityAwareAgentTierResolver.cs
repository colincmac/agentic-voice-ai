using System.Collections.Concurrent;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

/// <summary>
/// Capacity-aware tier resolver that tracks active session counts per tier
/// and selects the best available tier based on operator-configured limits.
/// </summary>
/// <remarks>
/// Uses <see cref="IOptionsMonitor{TOptions}"/> for hot-reload support so
/// operators can adjust tier limits without restarting the service.
/// </remarks>
public sealed class CapacityAwareAgentTierResolver : IAgentTierResolver
{
    private readonly IOptionsMonitor<AgentTierOptions> _optionsMonitor;
    private readonly ConcurrentDictionary<AgentTier, int> _activeCounts = new();
    private readonly ILogger<CapacityAwareAgentTierResolver> _logger;

    public CapacityAwareAgentTierResolver(
        IOptionsMonitor<AgentTierOptions> optionsMonitor,
        ILoggerFactory? loggerFactory = null)
    {
        _optionsMonitor = optionsMonitor;
        _logger = loggerFactory?.CreateLogger<CapacityAwareAgentTierResolver>()
                  ?? NullLogger<CapacityAwareAgentTierResolver>.Instance;

        // Pre-populate counters for all known tiers
        foreach (AgentTier tier in Enum.GetValues<AgentTier>())
        {
            _activeCounts.TryAdd(tier, 0);
        }
    }

    /// <inheritdoc/>
    public ValueTask<AgentTier> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var options = _optionsMonitor.CurrentValue;

        foreach (var tier in options.FallbackOrder)
        {
            if (IsTierAvailable(tier, options))
            {
                _logger.LogDebug(
                    "Resolved tier {Tier} (active: {Active}, max: {Max})",
                    tier,
                    GetActiveCount(tier),
                    GetMaxConcurrent(tier, options));

                return new ValueTask<AgentTier>(tier);
            }
        }

        // If all configured tiers are exhausted, fall back to DtmfOnly as the last resort
        _logger.LogWarning("All configured tiers are at capacity. Falling back to {Tier}", AgentTier.DtmfOnly);

        return new ValueTask<AgentTier>(AgentTier.DtmfOnly);
    }

    /// <inheritdoc/>
    public ValueTask<AgentTier?> ResolveFallbackAsync(AgentTier currentTier, CancellationToken cancellationToken = default)
    {
        var options = _optionsMonitor.CurrentValue;
        bool foundCurrent = false;

        foreach (var tier in options.FallbackOrder)
        {
            if (tier == currentTier)
            {
                foundCurrent = true;

                continue;
            }

            if (foundCurrent && IsTierAvailable(tier, options))
            {
                _logger.LogInformation(
                    "Resolved fallback from {CurrentTier} to {FallbackTier} (active: {Active}, max: {Max})",
                    currentTier,
                    tier,
                    GetActiveCount(tier),
                    GetMaxConcurrent(tier, options));

                return new ValueTask<AgentTier?>(tier);
            }
        }

        _logger.LogWarning("No fallback tier available below {CurrentTier}", currentTier);

        return new ValueTask<AgentTier?>(result: null);
    }

    /// <inheritdoc/>
    public void Acquire(AgentTier tier)
    {
        var newCount = _activeCounts.AddOrUpdate(tier, 1, static (_, current) => current + 1);
        _logger.LogDebug("Acquired tier {Tier}, active count: {Count}", tier, newCount);
    }

    /// <inheritdoc/>
    public void Release(AgentTier tier)
    {
        var newCount = _activeCounts.AddOrUpdate(tier, 0, static (_, current) => Math.Max(0, current - 1));
        _logger.LogDebug("Released tier {Tier}, active count: {Count}", tier, newCount);
    }

    /// <summary>
    /// Gets the current active session count for a tier. Exposed for diagnostics and testing.
    /// </summary>
    public int GetActiveCount(AgentTier tier) => _activeCounts.GetValueOrDefault(tier, 0);

    private bool IsTierAvailable(AgentTier tier, AgentTierOptions options)
    {
        if (!options.Tiers.TryGetValue(tier, out var config))
        {
            // Tier not configured — treat as enabled with unlimited capacity
            return true;
        }

        if (!config.Enabled)
        {
            return false;
        }

        if (config.MaxConcurrent is null)
        {
            return true;
        }

        var activeCount = GetActiveCount(tier);

        return activeCount < config.MaxConcurrent.Value;
    }

    private static string GetMaxConcurrent(AgentTier tier, AgentTierOptions options) =>
        options.Tiers.TryGetValue(tier, out var config) && config.MaxConcurrent.HasValue
            ? config.MaxConcurrent.Value.ToString()
            : "unlimited";
}
