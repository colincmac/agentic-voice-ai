using Agents.AI.ContactCenter.Calling.Strategies.Dtmf;
using Agents.AI.ContactCenter.Calling.Strategies.Nlu;
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

    /// <summary>
    /// Register an <see cref="NluCallWorkflowStrategy"/> factory bound to <paramref name="workflowId"/>.
    /// Requires an <see cref="Agents.IntentAgent.IvrIntentAgent"/> and
    /// <see cref="Agents.AI.ContactCenter.Media.Audio.ISpeechSynthesizer"/> in DI (the legacy
    /// <c>AddNluStrategy(...)</c> already wires the intent agent).
    /// </summary>
    public static CallSessionContainerBuilder AddNluCallWorkflowStrategy(
        this CallSessionContainerBuilder builder,
        string workflowId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(workflowId);

        builder.Services.TryAddSingleton<ICallWorkflowSessionFactory, CallWorkflowSessionFactory>();
        builder.Services.AddSingleton<Calling.IConversationStrategyFactory>(
            _ => new NluCallWorkflowStrategyFactory(workflowId));

        return builder;
    }

    /// <summary>
    /// Register a <see cref="DtmfCallWorkflowStrategy"/> factory bound to <paramref name="workflowId"/>.
    /// Requires an <see cref="Agents.AI.ContactCenter.Media.Audio.ISpeechSynthesizer"/> in DI for SSML/text playback.
    /// </summary>
    public static CallSessionContainerBuilder AddDtmfCallWorkflowStrategy(
        this CallSessionContainerBuilder builder,
        string workflowId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(workflowId);

        builder.Services.TryAddSingleton<ICallWorkflowSessionFactory, CallWorkflowSessionFactory>();
        builder.Services.AddSingleton<Calling.IConversationStrategyFactory>(
            _ => new DtmfCallWorkflowStrategyFactory(workflowId));

        return builder;
    }
}
