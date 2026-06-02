using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.Telemetry;
using Agents.AI.Extensions.AITools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Calling.Strategies.RealtimeVoice;

/// <summary>
/// Factory that constructs <see cref="RealtimeCallWorkflowStrategy"/> instances from a
/// pre-registered <see cref="IvrWorkflow.Compilation.CompiledCallWorkflow"/> resolved out
/// of the <see cref="ICallWorkflowCatalog"/> by id. Implements the
/// <see cref="IConversationStrategyFactory"/> contract so the existing
/// <c>CallSessionFactory</c> + composite-fallback infrastructure can drive it.
/// </summary>
public sealed class RealtimeCallWorkflowStrategyFactory : IConversationStrategyFactory
{
    private readonly string _workflowId;

    public RealtimeCallWorkflowStrategyFactory(string workflowId)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);
        _workflowId = workflowId;
    }

    public AgentTier Tier => AgentTier.RealtimeVoice;

    public ValueTask<IConversationStrategy> CreateAsync(
        string callId,
        IServiceProvider services,
        IvrWorkflowState? restoreFrom,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentNullException.ThrowIfNull(services);

        var catalog = services.GetRequiredService<ICallWorkflowCatalog>();
        var compiled = catalog.Get(_workflowId);

        var backend = services.GetRequiredService<IRealtimeVoiceBackend>();
        var toolProvider = services.GetRequiredService<INamedAIFunctionProvider>();
        var telemetry = services.GetRequiredService<CallingTelemetry>();
        var loggerFactory = services.GetService<ILoggerFactory>();
        var sessionFactory = services.GetService<ICallWorkflowSessionFactory>()
            ?? new CallWorkflowSessionFactory(loggerFactory);

        var session = sessionFactory.Create(compiled, services, restoreFrom);

        IConversationStrategy strategy = new RealtimeCallWorkflowStrategy(
            backend,
            session,
            toolProvider,
            telemetry,
            loggerFactory);

        return ValueTask.FromResult(strategy);
    }
}
