using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.LiveVoice.Media.Audio;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Implementation;


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
