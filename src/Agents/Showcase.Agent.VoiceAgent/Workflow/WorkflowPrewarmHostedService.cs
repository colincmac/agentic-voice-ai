using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow;

namespace Showcase.Agent.VoiceAgent.Workflow;

/// <summary>
/// Startup-time warm-up of the per-tier <see cref="IConversationStrategyFactory"/>
/// singletons and the keyed <see cref="RealtimeIvrWorkflowDefinition"/> registrations.
/// Replaces the inline <c>Task.Run(... PrewarmAsync ...)</c> the incoming-call endpoint
/// used to fire per call: the strategy factories are singletons, so simply resolving them
/// once at boot pays the JIT / static-init cost before any caller is parked on hold.
/// </summary>
/// <remarks>
/// This service deliberately does <b>not</b> call <see cref="ICallSessionFactory.PrewarmAsync"/>
/// because that API is keyed by <c>CallId</c> and there is no upcoming call to attach to at
/// startup. If per-call prewarm is ever needed again (e.g. to overlap realtime backend
/// connect with the ACS media handshake), prefer pushing it into <c>CallSessionFactory.CreateAsync</c>
/// rather than reintroducing it at the endpoint layer.
/// </remarks>
internal sealed class WorkflowPrewarmHostedService(
    IServiceProvider services,
    ILogger<WorkflowPrewarmHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var factories = services.GetServices<IConversationStrategyFactory>().ToList();
            foreach (var factory in factories)
            {
                logger.LogInformation(
                    "Prewarmed conversation strategy factory {Factory} for tier {Tier}",
                    factory.GetType().Name, factory.Tier);
            }

            foreach (var tier in Enum.GetValues<AgentTier>())
            {
                var workflow = services.GetKeyedService<RealtimeIvrWorkflowDefinition>(tier.ToString());
                if (workflow is not null)
                {
                    logger.LogInformation(
                        "Prewarmed workflow {Workflow} (tier {Tier})", workflow.Name, workflow.Tier);
                }
            }

            // Touch the default (non-keyed) workflow registration too, if any.
            var defaultWorkflow = services.GetService<RealtimeIvrWorkflowDefinition>();
            if (defaultWorkflow is not null)
            {
                logger.LogInformation(
                    "Prewarmed default workflow {Workflow} (tier {Tier})",
                    defaultWorkflow.Name, defaultWorkflow.Tier);
            }
        }
        catch (Exception ex)
        {
            // Prewarm is best-effort — never block app startup on it.
            logger.LogWarning(ex, "Workflow prewarm encountered an error; continuing startup");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
