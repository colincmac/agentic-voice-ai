using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.Media.Audio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Calling.Strategies.Dtmf;

/// <summary>
/// Factory that constructs <see cref="DtmfCallWorkflowStrategy"/> instances bound to a
/// pre-registered workflow id in the <see cref="ICallWorkflowCatalog"/>.
/// </summary>
public sealed class DtmfCallWorkflowStrategyFactory : IConversationStrategyFactory
{
    private readonly string _workflowId;

    public DtmfCallWorkflowStrategyFactory(string workflowId)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);
        _workflowId = workflowId;
    }

    public AgentTier Tier => AgentTier.DtmfOnly;

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

        var synthesizer = services.GetService<ISpeechSynthesizer>();
        var loggerFactory = services.GetService<ILoggerFactory>();
        var sessionFactory = services.GetService<ICallWorkflowSessionFactory>()
            ?? new CallWorkflowSessionFactory(loggerFactory);

        var session = sessionFactory.Create(compiled, services, restoreFrom);

        IConversationStrategy strategy = new DtmfCallWorkflowStrategy(
            session,
            synthesizer,
            loggerFactory);

        return ValueTask.FromResult(strategy);
    }
}
