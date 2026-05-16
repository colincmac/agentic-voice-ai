namespace Agents.AI.ContactCenter.Configuration;

/// <summary>
/// Root configuration for the hyperscale coordination plane (ADR-0010,
/// ADR-0011, ADR-0004). Sub-sections are added incrementally as the
/// primitives that consume them land.
/// </summary>
public sealed class HyperscaleOptions
{
    public const string SectionName = "Hyperscale";

    /// <summary>
    /// Cluster / pod / instance identity for this process. Drives ownership
    /// leases, capacity coordination, and telemetry tagging.
    /// </summary>
    public ClusterIdentityOptions ClusterIdentity { get; set; } = new();

    /// <summary>
    /// Webhook dedup token policy. Used by
    /// <see cref="Agents.AI.ContactCenter.Coordination.IWebhookIdempotencyStore"/>
    /// to short-circuit duplicate at-least-once mid-call callbacks per ADR-0004.
    /// </summary>
    public WebhookIdempotencyOptions WebhookIdempotency { get; set; } = new();

    /// <summary>
    /// Per-call ownership lease policy used by
    /// <see cref="Agents.AI.ContactCenter.Coordination.ICallOwnershipDirectory"/>
    /// to bind a call to the pod that holds its bi-di WS / verb session
    /// (ADR-0011).
    /// </summary>
    public CallOwnershipOptions CallOwnership { get; set; } = new();

    /// <summary>
    /// Per-cluster tier-ceiling policy used by
    /// <see cref="Agents.AI.ContactCenter.Coordination.ITierCeilingProvider"/>
    /// to broadcast and cache the active degradation ceiling (ADR-0008).
    /// </summary>
    public TierCeilingOptions TierCeiling { get; set; } = new();
}

/// <summary>
/// Configuration for <see cref="Agents.AI.ContactCenter.Coordination.IClusterIdentity"/>.
/// All values resolve from configuration first, then the documented
/// environment-variable fallbacks, then dev defaults.
/// </summary>
public sealed class ClusterIdentityOptions
{
    /// <summary>
    /// Environment variable consulted when <see cref="ClusterId"/> is unset.
    /// Set this in the Helm chart / deployment manifest per ADR-0010.
    /// </summary>
    public const string ClusterIdEnvironmentVariable = "HYPERSCALE_CLUSTER_ID";

    /// <summary>
    /// Environment variable consulted when <see cref="PodId"/> is unset and
    /// before falling back to the standard Kubernetes <c>HOSTNAME</c>.
    /// </summary>
    public const string PodIdEnvironmentVariable = "HYPERSCALE_POD_ID";

    /// <summary>
    /// Cluster identifier. When null or whitespace, falls back to env var
    /// <see cref="ClusterIdEnvironmentVariable"/>; if that is also unset,
    /// defaults to <c>"local"</c> for dev / single-cluster deployments.
    /// </summary>
    public string? ClusterId { get; set; }

    /// <summary>
    /// Pod identifier. When null or whitespace, falls back to env var
    /// <see cref="PodIdEnvironmentVariable"/>, then <c>HOSTNAME</c>
    /// (Kubernetes default), then <see cref="System.Environment.MachineName"/>.
    /// </summary>
    public string? PodId { get; set; }
}

/// <summary>
/// Configuration for <see cref="Agents.AI.ContactCenter.Coordination.IWebhookIdempotencyStore"/>.
/// </summary>
public sealed class WebhookIdempotencyOptions
{
    /// <summary>
    /// How long a dedup token persists. Must be at least the longest plausible
    /// retry window of the at-least-once webhook publisher. Default 30 minutes
    /// matches ADR-0004 for ACS Call Automation callbacks.
    /// </summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Configuration for <see cref="Agents.AI.ContactCenter.Coordination.ICallOwnershipDirectory"/>.
/// </summary>
public sealed class CallOwnershipOptions
{
    /// <summary>
    /// How long a per-call ownership lease lives before the directory
    /// considers the owner dead and the call reapable. The pod heartbeat
    /// (ADR-0011) renews owned-call leases at one-third of this interval, so
    /// a single missed heartbeat does not orphan calls. Default 90 s matches
    /// the lease window in ADR-0011.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(90);
}

/// <summary>
/// Configuration for <see cref="Agents.AI.ContactCenter.Coordination.ITierCeilingProvider"/>.
/// </summary>
public sealed class TierCeilingOptions
{
    /// <summary>
    /// Ceiling assumed at process start before the first read from Redis (or
    /// when no value has ever been set for the cluster). Default
    /// <see cref="AgentTier.RealtimeVoice"/> means "no degradation" — admit
    /// calls at the highest tier the per-tier capacity counter allows.
    /// </summary>
    public AgentTier DefaultCeiling { get; set; } = AgentTier.RealtimeVoice;
}
