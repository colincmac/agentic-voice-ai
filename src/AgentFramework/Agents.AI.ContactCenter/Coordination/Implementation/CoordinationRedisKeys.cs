namespace Agents.AI.ContactCenter.Coordination.Implementation;

/// <summary>
/// Hash-tagged Redis key formats for the coordination plane (ADR-0004).
/// The literal <c>{callConnectionId}</c> braces colocate every per-call key
/// on the same shard so cross-namespace operations for one call stay local.
/// Cross-call counters deliberately omit the tag so they spread across shards.
/// </summary>
internal static class CoordinationRedisKeys
{
    /// <summary>
    /// <c>dedup:{callConnectionId}:sequenceNumber</c> — webhook idempotency
    /// token; <c>SET … NX EX</c> with <c>WebhookIdempotencyOptions.TokenLifetime</c>.
    /// </summary>
    public static string Dedup(string callConnectionId, int sequenceNumber)
        => $"dedup:{{{callConnectionId}}}:{sequenceNumber}";
}
