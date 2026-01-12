using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.BackgroundAgents;

/// <summary>
/// Orchestrates background AI agents that assist the realtime voice agent during a session.
/// These agents can perform tasks like fraud detection, compliance monitoring, or provide contextual assistance.
/// </summary>
public sealed class BackgroundAgentOrchestrator : IAsyncDisposable
{
    private readonly ILogger<BackgroundAgentOrchestrator> _logger;
    private readonly ConcurrentDictionary<string, BackgroundAgentContext> _activeAgents = new();
    private readonly CancellationTokenSource _cts = new();

    public BackgroundAgentOrchestrator(ILogger<BackgroundAgentOrchestrator>? logger = null)
    {
        _logger = logger ?? NullLogger<BackgroundAgentOrchestrator>.Instance;
    }

    /// <summary>
    /// Registers a background agent for this session
    /// </summary>
    public async Task<string> RegisterAgentAsync(
        AIAgent agent,
        AgentThread? thread = null,
        BackgroundAgentRole role = BackgroundAgentRole.Assistant,
        CancellationToken cancellationToken = default)
    {
        var agentId = Guid.NewGuid().ToString("N");
        var context = new BackgroundAgentContext(agent, thread ?? agent.GetNewThread(), role);

        if (_activeAgents.TryAdd(agentId, context))
        {
            _logger.LogInformation(
                "Registered background agent {AgentId} with role {Role}",
                agentId, role);
            
            return agentId;
        }

        throw new InvalidOperationException("Failed to register background agent");
    }

    /// <summary>
    /// Sends a message to a specific background agent and gets a response
    /// </summary>
    public async Task<AgentRunResponse> SendToAgentAsync(
        string agentId,
        IEnumerable<ChatMessage> messages,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!_activeAgents.TryGetValue(agentId, out var context))
        {
            throw new InvalidOperationException($"Background agent {agentId} not found");
        }

        _logger.LogDebug("Sending message to background agent {AgentId}", agentId);
        
        return await context.Agent.RunAsync(messages, context.Thread, options, cancellationToken);
    }

    /// <summary>
    /// Broadcasts a message to all background agents with a specific role
    /// </summary>
    public async Task<IReadOnlyList<BackgroundAgentResponse>> BroadcastToRoleAsync(
        BackgroundAgentRole role,
        IEnumerable<ChatMessage> messages,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var agentsWithRole = _activeAgents
            .Where(kvp => kvp.Value.Role == role)
            .ToList();

        if (agentsWithRole.Count == 0)
        {
            return Array.Empty<BackgroundAgentResponse>();
        }

        _logger.LogDebug(
            "Broadcasting to {Count} background agents with role {Role}",
            agentsWithRole.Count, role);

        var tasks = agentsWithRole.Select(async kvp =>
        {
            try
            {
                var response = await kvp.Value.Agent.RunAsync(
                    messages, kvp.Value.Thread, options, cancellationToken);
                return new BackgroundAgentResponse(kvp.Key, response, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running background agent {AgentId}", kvp.Key);
                return new BackgroundAgentResponse(kvp.Key, null, ex);
            }
        });

        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Gets all active background agents
    /// </summary>
    public IReadOnlyDictionary<string, BackgroundAgentContext> GetActiveAgents()
        => _activeAgents;

    /// <summary>
    /// Unregisters a background agent
    /// </summary>
    public async Task<bool> UnregisterAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        if (_activeAgents.TryRemove(agentId, out var context))
        {
            _logger.LogInformation("Unregistered background agent {AgentId}", agentId);
            return true;
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _activeAgents.Clear();
        _cts.Dispose();
    }
}

public sealed class BackgroundAgentContext
{
    public BackgroundAgentContext(AIAgent agent, AgentThread thread, BackgroundAgentRole role)
    {
        Agent = agent;
        Thread = thread;
        Role = role;
        RegisteredAt = DateTimeOffset.UtcNow;
    }

    public AIAgent Agent { get; }
    public AgentThread Thread { get; }
    public BackgroundAgentRole Role { get; }
    public DateTimeOffset RegisteredAt { get; }
}

public record BackgroundAgentResponse(string AgentId, AgentRunResponse? Response, Exception? Error);

public enum BackgroundAgentRole
{
    /// <summary>
    /// General assistant agent
    /// </summary>
    Assistant,
    
    /// <summary>
    /// Monitors for fraud and suspicious activity
    /// </summary>
    FraudMonitor,
    
    /// <summary>
    /// Handles authorization and consent workflows
    /// </summary>
    Authorization,
    
    /// <summary>
    /// Performs compliance and regulatory checks
    /// </summary>
    Compliance,
    
    /// <summary>
    /// Evaluates conversation quality and metrics
    /// </summary>
    QualityMonitor,
    
    /// <summary>
    /// Performs voice biometric verification
    /// </summary>
    VoiceBiometrics
}
