using Agents.AI.ContactCenter.Agents.AuthorizationAgent;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Calling.Strategies.RealtimeVoice;

/// <summary>
/// Factory that constructs <see cref="RealtimeCallWorkflowStrategy"/> instances from the
/// <see cref="IvrWorkflow.Compilation.CompiledCallWorkflow"/> chosen for the active call.
/// The workflow is resolved per call via the scoped <see cref="CallWorkflowSelection"/>
/// (falling back to <see cref="_defaultWorkflowId"/> and then the single registered workflow),
/// so a single registration serves every workflow on the host. Implements the
/// <see cref="IConversationStrategyFactory"/> contract so the existing
/// <c>CallSessionFactory</c> + composite-fallback infrastructure can drive it.
/// </summary>
public sealed class RealtimeCallWorkflowStrategyFactory : IConversationStrategyFactory
{
    private readonly string? _defaultWorkflowId;

    public RealtimeCallWorkflowStrategyFactory(string? defaultWorkflowId = null)
    {
        _defaultWorkflowId = defaultWorkflowId;
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
        var selection = services.GetRequiredService<CallWorkflowSelection>();
        var compiled = selection.Resolve(catalog, _defaultWorkflowId);

        var agent = services.GetRequiredService<AuthorizingAIAgent>();
        var telemetry = services.GetRequiredService<CallingTelemetry>();
        var loggerFactory = services.GetService<ILoggerFactory>();
        var sessionFactory = services.GetService<ICallWorkflowSessionFactory>()
            ?? new CallWorkflowSessionFactory(loggerFactory);

        var session = sessionFactory.Create(compiled, services, restoreFrom);

        IConversationStrategy strategy = new RealtimeCallWorkflowStrategy(
            agent,
            session,
            telemetry,
            loggerFactory);

        return ValueTask.FromResult(strategy);
    }
}
