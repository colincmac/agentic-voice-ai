using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.Media.Audio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Calling.Strategies.Dtmf;

/// <summary>
/// Factory that constructs <see cref="DtmfCallWorkflowStrategy"/> instances from the
/// <see cref="IvrWorkflow.Compilation.CompiledCallWorkflow"/> chosen for the active call.
/// The workflow is resolved per call via the scoped <see cref="CallWorkflowSelection"/>
/// (falling back to <see cref="_defaultWorkflowId"/> and then the single registered workflow).
/// </summary>
public sealed class DtmfCallWorkflowStrategyFactory : IConversationStrategyFactory
{
    private readonly string? _defaultWorkflowId;

    public DtmfCallWorkflowStrategyFactory(string? defaultWorkflowId = null)
    {
        _defaultWorkflowId = defaultWorkflowId;
    }

    public AgentTier Tier => AgentTier.DtmfOnly;

    public ValueTask<IConversationStrategy> CreateAsync(
        string callId,
        IServiceProvider services,
        IvrWorkflowState? restoreFrom,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentNullException.ThrowIfNull(services);

        var catalog = services.GetRequiredService<ICallWorkflowCatalog>();
        var selection = services.GetRequiredService<CallWorkflowSelection>();
        var compiled = selection.Resolve(catalog, _defaultWorkflowId);

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
