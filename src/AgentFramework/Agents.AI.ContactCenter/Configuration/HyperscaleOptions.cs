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
