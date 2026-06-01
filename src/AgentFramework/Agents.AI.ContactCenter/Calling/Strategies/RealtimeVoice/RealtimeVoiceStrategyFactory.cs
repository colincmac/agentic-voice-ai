using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Telemetry;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Calling.Strategies.RealtimeVoice;

/// <summary>
/// Strategy factory that resolves an <see cref="IRealtimeVoiceBackend"/> from DI
/// (typically the production <c>AuthorizingAgentRealtimeBackend</c> adapter, or a
/// fake in tests) and wraps it in <see cref="RealtimeVoiceStrategy"/>.
/// </summary>
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
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var telemetry = services.GetRequiredService<CallingTelemetry>();
        var sessionFactory = services.GetService<IIvrWorkflowSessionFactory>() ?? new IvrWorkflowSessionFactory();

        var session = sessionFactory.Create(workflow, restoreFrom, services);
        IConversationStrategy strategy = new RealtimeVoiceStrategy(backend, session, loggerFactory, telemetry);
        return ValueTask.FromResult(strategy);
    }
}
