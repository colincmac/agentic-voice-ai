using System;
using System.Collections.Generic;
using Agents.AI.ContactCenter.Agents.IntentAgent;
using Agents.AI.ContactCenter.Media.Analysis;
using Agents.AI.ContactCenter.Media.Audio;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.DependencyInjection;

/// <summary>
/// DI helpers for registering <see cref="ChatClientIntentClassifier"/> and
/// <see cref="IvrIntentAgent"/> in a host container. Both helpers default to the
/// non-keyed <see cref="IChatClient"/> in DI, but accept a <c>chatClientKey</c>
/// so the showcase wiring (which registers a keyed chat client named
/// <c>"chat"</c>) can opt in without re-registering the underlying client.
/// </summary>
public static class IntentClassificationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ChatClientIntentClassifier"/> as the
    /// <see cref="IIntentClassifier"/> in DI. The classifier resolves an
    /// <see cref="IChatClient"/> from the container — keyed if
    /// <paramref name="chatClientKey"/> is supplied, otherwise the default
    /// registration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="chatClientKey">
    /// Optional service key to resolve the chat client from. When omitted, the
    /// default (non-keyed) <see cref="IChatClient"/> registration is used.
    /// </param>
    /// <param name="configure">Optional callback to tweak classifier options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddChatClientIntentClassifier(
        this IServiceCollection services,
        object? chatClientKey = null,
        Action<ChatClientIntentClassifierOptions>? configure = null)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        var optionsBuilder = services.AddOptions<ChatClientIntentClassifierOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.Replace(ServiceDescriptor.Singleton<IIntentClassifier>(sp =>
        {
            var chatClient = chatClientKey is null
                ? sp.GetRequiredService<IChatClient>()
                : sp.GetRequiredKeyedService<IChatClient>(chatClientKey);
            var options = sp.GetService<Microsoft.Extensions.Options.IOptions<ChatClientIntentClassifierOptions>>()?.Value;
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger<ChatClientIntentClassifier>();
            return new ChatClientIntentClassifier(chatClient, options, logger);
        }));

        return services;
    }

    /// <summary>
    /// Registers <see cref="IvrIntentAgent"/> as a keyed singleton (when
    /// <paramref name="agentKey"/> is supplied) or as the default <see cref="AIAgent"/>
    /// registration. The agent resolves its <see cref="IIntentClassifier"/> dependency
    /// from DI (typically <see cref="ChatClientIntentClassifier"/> registered via
    /// <see cref="AddChatClientIntentClassifier"/>) and uses
    /// <see cref="ISpeechRecognizer"/> if available for the audio-streaming surface.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional callback to configure the agent (name, default intents…).</param>
    /// <param name="agentKey">
    /// Optional DI key. When supplied, the agent is registered as a keyed singleton; when
    /// omitted, it is registered as the default <see cref="IvrIntentAgent"/> service.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddIvrIntentAgent(
        this IServiceCollection services,
        Action<IvrIntentAgentOptions>? configureOptions = null,
        object? agentKey = null)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        var optionsBuilder = services.AddOptions<IvrIntentAgentOptions>();
        if (configureOptions is not null)
        {
            optionsBuilder.Configure(configureOptions);
        }

        IvrIntentAgent Factory(IServiceProvider sp)
        {
            var classifier = sp.GetRequiredService<IIntentClassifier>();
            var recognizer = sp.GetService<ISpeechRecognizer>();
            Func<ISpeechRecognizer>? recognizerFactory = recognizer is null
                ? sp.GetService<Func<ISpeechRecognizer>>()
                : () => sp.GetRequiredService<ISpeechRecognizer>();
            var options = sp.GetService<Microsoft.Extensions.Options.IOptions<IvrIntentAgentOptions>>()?.Value;
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new IvrIntentAgent(classifier, recognizerFactory, options, loggerFactory);
        }

        if (agentKey is null)
        {
            services.TryAddSingleton(Factory);
        }
        else
        {
            services.TryAddKeyedSingleton<IvrIntentAgent>(agentKey, (sp, _) => Factory(sp));
        }

        return services;
    }
}
