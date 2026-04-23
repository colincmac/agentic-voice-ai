using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Agents.AI.RealtimeVoice.Azure.Transports;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

/// <summary>
/// Factory for creating <see cref="IChannelTransport"/> instances for a specific <see cref="AgentTier"/>.
/// Each tier has its own factory implementation that knows how to resolve the
/// correct agents, models, and speech services from DI.
/// </summary>
public interface IAgentTransportFactory
{
    /// <summary>
    /// Gets the <see cref="AgentTier"/> this factory creates transports for.
    /// </summary>
    AgentTier Tier { get; }

    /// <summary>
    /// Creates a transport for a session, resolving dependencies from the session-scoped service provider.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="sessionServices">The session-scoped service provider.</param>
    /// <param name="workflow">The IVR workflow definition for the session.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A connected transport and optional workflow state (for state restoration on failover).</returns>
    ValueTask<AgentTransportResult> CreateAsync(
        string sessionId,
        IServiceProvider sessionServices,
        RealtimeIvrWorkflowDefinition workflow,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of creating an agent transport, including the transport itself
/// and the workflow state it operates on.
/// </summary>
public sealed class AgentTransportResult
{
    /// <summary>
    /// The created transport.
    /// </summary>
    public required IChannelTransport Transport { get; init; }

    /// <summary>
    /// The workflow state associated with this transport.
    /// Used for state capture during mid-call failover.
    /// </summary>
    public IvrWorkflowState? WorkflowState { get; init; }

    /// <summary>
    /// The tier this transport was created for.
    /// </summary>
    public required AgentTier Tier { get; init; }
}
