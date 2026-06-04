using Agents.AI.ContactCenter.Agents.AuthorizationAgent;
using Agents.AI.ContactCenter.Agents.IntentAgent;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Strategies.Dtmf;
using Agents.AI.ContactCenter.Calling.Strategies.Nlu;
using Agents.AI.ContactCenter.Calling.Strategies.RealtimeVoice;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.Extensions.ToolApproval;
using Agents.AI.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using Agents.AI.ContactCenter.Media.Audio;
using Microsoft.Extensions.Options;

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
    /// Register a <see cref="RealtimeCallWorkflowStrategy"/> factory. The workflow is selected
    /// per call via <c>CallSessionRequest.WorkflowId</c>; <paramref name="defaultWorkflowId"/> is
    /// used when the request omits one, falling back to the single registered workflow when the
    /// catalog is unambiguous. The caller must also register the realtime backend
    /// (<c>builder.AddRealtimeVoiceStrategy(...)</c> already does this), the
    /// <see cref="Agents.AI.Extensions.AITools.INamedAIFunctionProvider"/>, and the workflow
    /// blueprint(s) (via <c>services.AddCallWorkflow(...)</c>).
    /// </summary>
    public static CallSessionContainerBuilder AddRealtimeCallWorkflowStrategy(
        this CallSessionContainerBuilder builder,
        string? defaultWorkflowId = null,
        string? realtimeAgentServiceKey = null,
        RealtimeAgentRunOptions? runOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<IToolApprovalStore, InMemoryToolApprovalStore>();
        builder.Services.TryAddScoped<IToolApprovalHandlerProvider, ToolApprovalHandlerProvider>();
        builder.Services.TryAddScoped<IToolApprovalHandler, RequiresCallerVerificationHandler>();

        builder.Services.TryAddScoped(sp =>
        {
            var agent = !string.IsNullOrEmpty(realtimeAgentServiceKey)
                ? sp.GetRequiredKeyedService<RealtimeAIAgent>(realtimeAgentServiceKey)
                : sp.GetRequiredService<RealtimeAIAgent>();

            return new AuthorizingAIAgent(
                agent,
                serviceProvider: sp);
        });

        builder.Services.AddTransient<IRealtimeVoiceBackend>(sp =>
        {
            var agent = sp.GetRequiredService<AuthorizingAIAgent>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new AIAgentBackend(agent, runOptions: runOptions, loggerFactory);
        });

        builder.Services.TryAddSingleton<ICallWorkflowSessionFactory, CallWorkflowSessionFactory>();
        builder.Services.AddSingleton<IConversationStrategyFactory>(
            _ => new RealtimeCallWorkflowStrategyFactory(defaultWorkflowId));

        return builder;
    }

    /// <summary>
    /// Register an <see cref="NluCallWorkflowStrategy"/> factory. The workflow is selected per
    /// call via <c>CallSessionRequest.WorkflowId</c>; <paramref name="defaultWorkflowId"/> is used
    /// when the request omits one, falling back to the single registered workflow when unambiguous.
    /// Requires an <see cref="Agents.IntentAgent.IvrIntentAgent"/> and
    /// <see cref="Agents.AI.ContactCenter.Media.Audio.ISpeechSynthesizer"/> in DI (the legacy
    /// <c>AddNluStrategy(...)</c> already wires the intent agent).
    /// </summary>
    public static CallSessionContainerBuilder AddNluCallWorkflowStrategy(
        this CallSessionContainerBuilder builder,
        string? defaultWorkflowId = null,
        string? chatClientServiceKey = null,
        Action<IvrIntentAgentOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new IvrIntentAgentOptions();
        configureOptions?.Invoke(options);

        builder.Services
            .AddOptions<IvrIntentAgentOptions>()
            .Configure(o => configureOptions?.Invoke(o))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.HostApplicationBuilder.AddAIAgent(options.Name, (sp, key) =>
        {
            var chatClient = chatClientServiceKey is null
                ? sp.GetRequiredService<IChatClient>()
                : sp.GetRequiredKeyedService<IChatClient>(chatClientServiceKey);

            var recognizer = sp.GetService<ISpeechRecognizer>();
            var resolvedOptions = sp.GetRequiredService<IOptions<IvrIntentAgentOptions>>().Value;
            var loggerFactory = sp.GetService<ILoggerFactory>();

            return new IvrIntentAgent(chatClient, recognizer, resolvedOptions, loggerFactory);
        });

        builder.Services.TryAddSingleton<IvrIntentAgent>(sp => sp.GetRequiredKeyedService<IvrIntentAgent>(options.Name));


        builder.Services.TryAddSingleton<ICallWorkflowSessionFactory, CallWorkflowSessionFactory>();
        builder.Services.AddSingleton<Calling.IConversationStrategyFactory>(
            _ => new NluCallWorkflowStrategyFactory(defaultWorkflowId));

        return builder;
    }

    /// <summary>
    /// Register a <see cref="DtmfCallWorkflowStrategy"/> factory. The workflow is selected per
    /// call via <c>CallSessionRequest.WorkflowId</c>; <paramref name="defaultWorkflowId"/> is used
    /// when the request omits one, falling back to the single registered workflow when unambiguous.
    /// Requires an <see cref="Agents.AI.ContactCenter.Media.Audio.ISpeechSynthesizer"/> in DI for SSML/text playback.
    /// </summary>
    public static CallSessionContainerBuilder AddDtmfCallWorkflowStrategy(
        this CallSessionContainerBuilder builder,
        string? defaultWorkflowId = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<ICallWorkflowSessionFactory, CallWorkflowSessionFactory>();
        builder.Services.AddSingleton<Calling.IConversationStrategyFactory>(
            _ => new DtmfCallWorkflowStrategyFactory(defaultWorkflowId));

        return builder;
    }
}
