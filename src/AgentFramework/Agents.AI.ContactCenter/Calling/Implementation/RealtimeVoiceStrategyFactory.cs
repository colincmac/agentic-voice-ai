using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Calling.Implementation;

/// <summary>
/// Strategy factory that resolves an <see cref="IRealtimeVoiceBackend"/> from DI
/// (typically the production <c>AuthorizingAgentRealtimeBackend</c> adapter, or a
/// fake in tests) and wraps it in <see cref="RealtimeVoiceStrategy"/>.
/// </summary>
/// <remarks>
/// Production wiring will register the adapter. The adapter itself is intentionally
/// not part of this slice — it would wrap <see cref="AuthorizingRealtimeAIAgent"/>
/// the same way <see cref="Transports.RealtimeVoiceAgentTransport"/> does today.
/// </remarks>
public sealed class RealtimeVoiceStrategyFactory : IConversationStrategyFactory
{
    public AgentTier Tier => AgentTier.RealtimeVoice;

    public ValueTask<IConversationStrategy> CreateAsync(
        string callId,
        IServiceProvider services,
        RealtimeIvrWorkflowDefinition workflow,
        IvrWorkflowState? restoreFrom,
        CancellationToken cancellationToken = default)
    {
        var backend = services.GetRequiredService<IRealtimeVoiceBackend>();
        var loggerFactory = services.GetService<ILoggerFactory>();

        IConversationStrategy strategy = new RealtimeVoiceStrategy(backend, workflow, restoreFrom, loggerFactory);
        return ValueTask.FromResult(strategy);
    }
}
