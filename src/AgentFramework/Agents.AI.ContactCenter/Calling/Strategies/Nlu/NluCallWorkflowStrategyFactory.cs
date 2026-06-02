using Agents.AI.ContactCenter.Agents.IntentAgent;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.Media.Audio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Calling.Strategies.Nlu;

/// <summary>
/// Factory that constructs <see cref="NluCallWorkflowStrategy"/> instances bound to a
/// pre-registered workflow id in the <see cref="ICallWorkflowCatalog"/>. Implements the
/// legacy <see cref="IConversationStrategyFactory"/> contract so the existing
/// <c>CallSessionFactory</c> + composite-fallback infrastructure can drive it unchanged.
/// </summary>
public sealed class NluCallWorkflowStrategyFactory : IConversationStrategyFactory
{
    private readonly string _workflowId;

    public NluCallWorkflowStrategyFactory(string workflowId)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);
        _workflowId = workflowId;
    }

    public AgentTier Tier => AgentTier.IntentNlu;

    public ValueTask<IConversationStrategy> CreateAsync(
        string callId,
        IServiceProvider services,
        RealtimeIvrWorkflowDefinition workflow,
        IvrWorkflowState? restoreFrom,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentNullException.ThrowIfNull(services);

        var catalog = services.GetRequiredService<ICallWorkflowCatalog>();
        var compiled = catalog.Get(_workflowId);

        var intentAgent = services.GetRequiredService<IvrIntentAgent>();
        var synthesizer = services.GetRequiredService<ISpeechSynthesizer>();
        var escalation = services.GetService<TransferEscalationTarget>();
        var loggerFactory = services.GetService<ILoggerFactory>();
        var sessionFactory = services.GetService<ICallWorkflowSessionFactory>()
            ?? new CallWorkflowSessionFactory(loggerFactory);

        var session = sessionFactory.Create(compiled, services, restoreFrom);

        IConversationStrategy strategy = new NluCallWorkflowStrategy(
            session,
            intentAgent,
            synthesizer,
            escalation,
            loggerFactory);

        return ValueTask.FromResult(strategy);
    }
}
