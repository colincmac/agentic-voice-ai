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

    /// <summary>
    /// <c>owner:{callConnectionId}</c> — call ownership lease per ADR-0011.
    /// Value is the <see cref="CallOwnershipCodec"/>-encoded
    /// <see cref="Coordination.CallOwnership"/>; TTL matches
    /// <c>CallOwnershipOptions.LeaseDuration</c>.
    /// </summary>
    public static string Ownership(string callConnectionId)
        => $"owner:{{{callConnectionId}}}";

    /// <summary>
    /// <c>ceiling:cluster:{clusterId}</c> — active tier ceiling for the cluster
    /// per ADR-0008. Used both as the string key (<c>SET</c> / <c>GET</c>) and
    /// as the Pub/Sub channel name; the published message body is the
    /// <c>(int)</c>-coded <see cref="Configuration.AgentTier"/>.
    /// </summary>
    public static string ClusterTierCeiling(string clusterId)
        => $"ceiling:cluster:{{{clusterId}}}";
}
