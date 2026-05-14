using Agents.AI.ContactCenter.Configuration;

namespace Agents.AI.ContactCenter.Calling;

/// <summary>
/// Resolves the best available agent tier for a new session based on
/// current capacity, endpoint health, and operator configuration.
/// </summary>
public interface IAgentTierResolver
{
    /// <summary>
    /// Resolves the best available tier for a new session by walking the
    /// configured fallback order and selecting the first tier with available capacity.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The selected <see cref="AgentTier"/>.</returns>
    ValueTask<AgentTier> ResolveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the next available fallback tier below the given current tier.
    /// Used for mid-call degradation when a transport fails.
    /// </summary>
    /// <param name="currentTier">The tier that failed or exceeded capacity.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The next available <see cref="AgentTier"/>, or null if no fallback is available.</returns>
    ValueTask<AgentTier?> ResolveFallbackAsync(AgentTier currentTier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Increments the active session count for the specified tier.
    /// Call this when a session is created at a given tier.
    /// </summary>
    void Acquire(AgentTier tier);

    /// <summary>
    /// Decrements the active session count for the specified tier.
    /// Call this when a session at a given tier is disposed.
    /// </summary>
    void Release(AgentTier tier);
}
