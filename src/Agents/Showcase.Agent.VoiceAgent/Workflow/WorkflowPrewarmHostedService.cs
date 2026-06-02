using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;

namespace Showcase.Agent.VoiceAgent.Workflow;

/// <summary>
/// Startup-time warm-up of the per-tier <see cref="IConversationStrategyFactory"/>
/// singletons and the compiled <see cref="ICallWorkflowCatalog"/>. Resolving the catalog
/// once forces every <see cref="IvrWorkflow.Blueprint.WorkflowBlueprint"/> through the
/// <see cref="IvrWorkflow.Compilation.WorkflowGraphCompiler"/> so authoring errors fail
/// the host on boot rather than on the first call.
/// </summary>
/// <remarks>
/// Replaces the prior service that warmed legacy keyed <c>RealtimeIvrWorkflowDefinition</c>
/// registrations. Best-effort: never blocks startup on failure.
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

            var catalog = services.GetService<ICallWorkflowCatalog>();
            if (catalog is not null)
            {
                foreach (var workflow in catalog.Workflows)
                {
                    logger.LogInformation(
                        "Prewarmed call workflow {WorkflowId} v{Version} ({StageCount} stages)",
                        workflow.Id, workflow.Version, workflow.Stages.Count);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Workflow prewarm encountered an error; continuing startup");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
