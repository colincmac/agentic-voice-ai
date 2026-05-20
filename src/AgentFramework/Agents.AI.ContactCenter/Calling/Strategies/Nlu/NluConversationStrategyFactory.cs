using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agents.AI.ContactCenter.Media.Analysis;

namespace Agents.AI.ContactCenter.Calling.Strategies.Nlu;

/// <summary>
/// Factory for the Tier 3 <see cref="NluConversationStrategy"/>. Resolves the
/// <see cref="ISpeechRecognizer"/>, <see cref="ISpeechSynthesizer"/>, and
/// <see cref="IIntentClassifier"/> registered in DI. The optional
/// <see cref="TransferEscalationTarget"/> is resolved from DI as a convenience so the
/// host can configure escalation once and have every NLU instance pick it up.
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
        var recognizer = services.GetRequiredService<ISpeechRecognizer>();
        var synthesizer = services.GetRequiredService<ISpeechSynthesizer>();
        var classifier = services.GetRequiredService<IIntentClassifier>();
        var escalation = services.GetService<TransferEscalationTarget>();
        var loggerFactory = services.GetService<ILoggerFactory>();

        IConversationStrategy strategy = new NluConversationStrategy(
            workflow,
            recognizer,
            synthesizer,
            classifier,
            restoreFrom,
            escalation,
            loggerFactory);
        return ValueTask.FromResult(strategy);
    }
}
