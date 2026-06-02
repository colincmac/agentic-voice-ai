using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agents.AI.ContactCenter.Calling.Core;

namespace Agents.AI.ContactCenter.Calling.Strategies.Dtmf;


public sealed class DtmfStreamingStrategyFactory : IConversationStrategyFactory
{
    public AgentTier Tier => AgentTier.DtmfOnly;

    public ValueTask<IConversationStrategy> CreateAsync(
        string callId,
        IServiceProvider services,
        RealtimeIvrWorkflowDefinition? workflow,
        IvrWorkflowState? restoreFrom,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow,
            "The legacy DtmfStreamingStrategyFactory requires a non-null workflow. Use DtmfCallWorkflowStrategyFactory + ICallWorkflowCatalog for the new model.");

        var synthesizer = services.GetRequiredService<ISpeechSynthesizer>();
        var loggerFactory = services.GetService<ILoggerFactory>();
        var sessionFactory = services.GetService<IIvrWorkflowSessionFactory>() ?? new IvrWorkflowSessionFactory();
        var session = sessionFactory.Create(workflow, restoreFrom, services);
        IConversationStrategy strategy = new DtmfStreamingStrategy(session, synthesizer, loggerFactory);
        return ValueTask.FromResult(strategy);
    }
}

public sealed class DtmfVerbStrategyFactory : IConversationStrategyFactory
{
    public AgentTier Tier => AgentTier.DtmfOnly;

    public ValueTask<IConversationStrategy> CreateAsync(
        string callId,
        IServiceProvider services,
        RealtimeIvrWorkflowDefinition? workflow,
        IvrWorkflowState? restoreFrom,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow,
            "The legacy DtmfVerbStrategyFactory requires a non-null workflow.");

        var loggerFactory = services.GetService<ILoggerFactory>();
        var sessionFactory = services.GetService<IIvrWorkflowSessionFactory>() ?? new IvrWorkflowSessionFactory();
        var session = sessionFactory.Create(workflow, restoreFrom, services);
        IConversationStrategy strategy = new DtmfVerbStrategy(session, loggerFactory);
        return ValueTask.FromResult(strategy);
    }
}
