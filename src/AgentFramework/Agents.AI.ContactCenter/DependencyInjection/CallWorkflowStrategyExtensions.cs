using Agents.AI.ContactCenter.Calling.Strategies.RealtimeVoice;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agents.AI.ContactCenter.DependencyInjection;

/// <summary>
/// DI extensions for wiring Phase-5 strategies that operate on the new
/// <see cref="IvrWorkflow.Blueprint.WorkflowBlueprint"/> / <see cref="IvrWorkflow.Compilation.CompiledCallWorkflow"/>
/// model. Live alongside the legacy <c>AddRealtimeVoiceStrategy</c> et al. until the
/// migration is complete.
/// </summary>
public static class CallWorkflowStrategyExtensions
{
    /// <summary>
    /// Register a <see cref="RealtimeCallWorkflowStrategy"/> factory that resolves
    /// <paramref name="workflowId"/> from the <see cref="IvrWorkflow.Catalog.ICallWorkflowCatalog"/>
    /// on every call. The caller must also register the realtime backend
    /// (<c>builder.AddRealtimeVoiceStrategy(...)</c> already does this), the
    /// <see cref="Agents.AI.Extensions.AITools.INamedAIFunctionProvider"/>, and the workflow
    /// blueprint itself (via <c>services.AddCallWorkflow(...)</c>).
    /// </summary>
    public static CallSessionContainerBuilder AddRealtimeCallWorkflowStrategy(
        this CallSessionContainerBuilder builder,
        string workflowId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(workflowId);

        builder.Services.TryAddSingleton<ICallWorkflowSessionFactory, CallWorkflowSessionFactory>();
        builder.Services.AddSingleton<Calling.IConversationStrategyFactory>(
            _ => new RealtimeCallWorkflowStrategyFactory(workflowId));

        return builder;
    }
}
