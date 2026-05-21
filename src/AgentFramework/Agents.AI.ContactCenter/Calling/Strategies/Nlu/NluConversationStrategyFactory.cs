using Agents.AI.ContactCenter.Agents.IntentAgent;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Calling.Strategies.Nlu;

/// <summary>
/// Factory for the Tier 3 <see cref="NluConversationStrategy"/>. Resolves the per-call
/// <see cref="IvrIntentAgent"/> (which owns speech recognition + JSON intent
/// classification) and the <see cref="ISpeechSynthesizer"/> registered in DI. The
/// optional <see cref="TransferEscalationTarget"/> is also resolved from DI so the host
/// can configure escalation once and have every NLU instance pick it up.
/// </summary>
public sealed class NluConversationStrategyFactory : IConversationStrategyFactory
{
    public AgentTier Tier => AgentTier.IntentNlu;

    public ValueTask<IConversationStrategy> CreateAsync(
        string callId,
        IServiceProvider services,
        RealtimeIvrWorkflowDefinition workflow,
        IvrWorkflowState? restoreFrom,
        CancellationToken cancellationToken = default)
    {
        var intentAgent = services.GetRequiredService<IvrIntentAgent>();
        var synthesizer = services.GetRequiredService<ISpeechSynthesizer>();
        var escalation = services.GetService<TransferEscalationTarget>();
        var loggerFactory = services.GetService<ILoggerFactory>();

        IConversationStrategy strategy = new NluConversationStrategy(
            workflow,
            intentAgent,
            synthesizer,
            restoreFrom,
            escalation,
            loggerFactory);
        return ValueTask.FromResult(strategy);
    }
}
