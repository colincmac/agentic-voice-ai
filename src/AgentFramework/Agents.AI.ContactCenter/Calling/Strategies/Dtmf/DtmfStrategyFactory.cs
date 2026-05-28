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
        RealtimeIvrWorkflowDefinition workflow,
        IvrWorkflowState? restoreFrom,
        CancellationToken cancellationToken = default)
    {
        var synthesizer = services.GetRequiredService<ISpeechSynthesizer>();
        var loggerFactory = services.GetService<ILoggerFactory>();
        IConversationStrategy strategy = new DtmfStreamingStrategy(workflow, synthesizer, restoreFrom, loggerFactory);
        return ValueTask.FromResult(strategy);
    }
}

public sealed class DtmfVerbStrategyFactory : IConversationStrategyFactory
{
    public AgentTier Tier => AgentTier.DtmfOnly;

    public ValueTask<IConversationStrategy> CreateAsync(
        string callId,
        IServiceProvider services,
        RealtimeIvrWorkflowDefinition workflow,
        IvrWorkflowState? restoreFrom,
        CancellationToken cancellationToken = default)
    {
        var loggerFactory = services.GetService<ILoggerFactory>();
        IConversationStrategy strategy = new DtmfVerbStrategy(workflow, restoreFrom, loggerFactory);
        return ValueTask.FromResult(strategy);
    }
}
