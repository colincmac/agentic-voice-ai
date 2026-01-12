using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Azure;
using Azure.AI.Inference;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OllamaSharp;
using Aspire.OpenAI;
using OpenAI;
using Microsoft.Extensions.Logging;
using Extensions.AI.RealtimeVoice;
using Microsoft.Shared.Diagnostics;
using Azure.AI.VoiceLive;
using Extensions.AI.RealtimeVoice.AzureVoiceLive;

namespace Agents.AI.Hosting;


public class ChatClientConnectionInfo
{
    public Uri? Endpoint { get; init; }
    public required string SelectedModel { get; init; }

    public ClientChatProvider Provider { get; init; }
    public string? AccessKey { get; init; }

    // Example connection string:
    // Endpoint=https://localhost:4523;Model=phi3.5;AccessKey=1234;Provider=ollama;
    public static bool TryParse(string? connectionString, [NotNullWhen(true)] out ChatClientConnectionInfo? settings)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            settings = null;
            return false;
        }

        var connectionBuilder = new DbConnectionStringBuilder
        {
            ConnectionString = connectionString
        };

        Uri? endpoint = null;
        if (connectionBuilder.ContainsKey("Endpoint") && Uri.TryCreate(connectionBuilder["Endpoint"].ToString(), UriKind.Absolute, out endpoint))
        {
        }

        string? model = null;
        if (connectionBuilder.ContainsKey("Model"))
        {
            model = (string)connectionBuilder["Model"];
        }

        string? accessKey = null;
        if (connectionBuilder.ContainsKey("Key"))
        {
            accessKey = (string)connectionBuilder["Key"];
        }

        var provider = ClientChatProvider.Unknown;
        if (connectionBuilder.ContainsKey("Provider"))
        {
            var providerValue = (string)connectionBuilder["Provider"];
            Enum.TryParse(providerValue, ignoreCase: true, out provider);
        }

        if (endpoint is null && provider != ClientChatProvider.OpenAI || model is null || provider == ClientChatProvider.Unknown)
        {
            settings = null;
            return false;
        }

        settings = new ChatClientConnectionInfo
        {
            Endpoint = endpoint,
            SelectedModel = model,
            AccessKey = accessKey,
            Provider = provider
        };

        return true;
    }
}

public enum ClientChatProvider
{
    Unknown,
    Ollama,
    OpenAI,
    AzureOpenAI,
    AzureAIInference,
    AzureVoiceLive
}
public static class LiveConversationClientBuilderExtensions
{
    /// <summary>Registers a singleton <see cref="ILiveConversationClient"/> in the <see cref="IServiceCollection"/>.</summary>
    /// <param name="serviceCollection">The <see cref="IServiceCollection"/> to which the client should be added.</param>
    /// <param name="innerClient">The inner <see cref="ILiveConversationClient"/> that represents the underlying backend.</param>
    /// <param name="lifetime">The service lifetime for the client. Defaults to <see cref="ServiceLifetime.Singleton"/>.</param>
    /// <returns>A <see cref="LiveConversationClientBuilder"/> that can be used to build a pipeline around the inner client.</returns>
    /// <remarks>The client is registered as a singleton service.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="serviceCollection"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="innerClient"/> is <see langword="null"/>.</exception>
    public static LiveConversationClientBuilder AddConversationClient(
        this IServiceCollection serviceCollection,
        ILiveConversationClient innerClient,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        _ = Throw.IfNull(serviceCollection);
        _ = Throw.IfNull(innerClient);

        return AddConversationClient(serviceCollection, _ => innerClient, lifetime);
    }

    /// <summary>Registers a singleton <see cref="ILiveConversationClient"/> in the <see cref="IServiceCollection"/>.</summary>
    /// <param name="serviceCollection">The <see cref="IServiceCollection"/> to which the client should be added.</param>
    /// <param name="innerClientFactory">A callback that produces the inner <see cref="ILiveConversationClient"/> that represents the underlying backend.</param>
    /// <param name="lifetime">The service lifetime for the client. Defaults to <see cref="ServiceLifetime.Singleton"/>.</param>
    /// <returns>A <see cref="LiveConversationClientBuilder"/> that can be used to build a pipeline around the inner client.</returns>
    /// <remarks>The client is registered as a singleton service.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="serviceCollection"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="innerClientFactory"/> is <see langword="null"/>.</exception>
    public static LiveConversationClientBuilder AddConversationClient(
        this IServiceCollection serviceCollection,
        Func<IServiceProvider, ILiveConversationClient> innerClientFactory,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        _ = Throw.IfNull(serviceCollection);
        _ = Throw.IfNull(innerClientFactory);

        var builder = new LiveConversationClientBuilder(innerClientFactory);
        serviceCollection.Add(new ServiceDescriptor(typeof(ILiveConversationClient), builder.Build, lifetime));
        return builder;
    }

    /// <summary>Registers a keyed singleton <see cref="ILiveConversationClient"/> in the <see cref="IServiceCollection"/>.</summary>
    /// <param name="serviceCollection">The <see cref="IServiceCollection"/> to which the client should be added.</param>
    /// <param name="serviceKey">The key with which to associate the client.</param>
    /// <param name="innerClient">The inner <see cref="ILiveConversationClient"/> that represents the underlying backend.</param>
    /// <param name="lifetime">The service lifetime for the client. Defaults to <see cref="ServiceLifetime.Singleton"/>.</param>
    /// <returns>A <see cref="LiveConversationClientBuilder"/> that can be used to build a pipeline around the inner client.</returns>
    /// <remarks>The client is registered as a scoped service.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="serviceCollection"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="innerClient"/> is <see langword="null"/>.</exception>
    public static LiveConversationClientBuilder AddKeyedConversationClient(
        this IServiceCollection serviceCollection,
        object? serviceKey,
        ILiveConversationClient innerClient,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        _ = Throw.IfNull(serviceCollection);
        _ = Throw.IfNull(innerClient);

        return AddKeyedConversationClient(serviceCollection, serviceKey, _ => innerClient, lifetime);
    }

    /// <summary>Registers a keyed singleton <see cref="ILiveConversationClient"/> in the <see cref="IServiceCollection"/>.</summary>
    /// <param name="serviceCollection">The <see cref="IServiceCollection"/> to which the client should be added.</param>
    /// <param name="serviceKey">The key with which to associate the client.</param>
    /// <param name="innerClientFactory">A callback that produces the inner <see cref="ILiveConversationClient"/> that represents the underlying backend.</param>
    /// <param name="lifetime">The service lifetime for the client. Defaults to <see cref="ServiceLifetime.Singleton"/>.</param>
    /// <returns>A <see cref="LiveConversationClientBuilder"/> that can be used to build a pipeline around the inner client.</returns>
    /// <remarks>The client is registered as a scoped service.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="serviceCollection"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="innerClientFactory"/> is <see langword="null"/>.</exception>
    public static LiveConversationClientBuilder AddKeyedConversationClient(
        this IServiceCollection serviceCollection,
        object? serviceKey,
        Func<IServiceProvider, ILiveConversationClient> innerClientFactory,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        _ = Throw.IfNull(serviceCollection);
        _ = Throw.IfNull(innerClientFactory);

        var builder = new LiveConversationClientBuilder(innerClientFactory);
        serviceCollection.Add(new ServiceDescriptor(typeof(ILiveConversationClient), serviceKey, factory: (services, serviceKey) => builder.Build(services), lifetime));
        return builder;
    }
    public static LiveConversationClientBuilder AddKeyedConversationClient(
    this AspireOpenAIClientBuilder builder,
    string serviceKey,
    string deploymentName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(serviceKey);

        return builder.HostBuilder.Services.AddKeyedConversationClient(
            serviceKey,
            services => CreateInnerRealtimeClient(services, builder, deploymentName));
    }
    public static LiveConversationClientBuilder AddKeyedConversationVoiceLiveClient(
    this AspireOpenAIClientBuilder builder,
    string serviceKey,
    string deploymentName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(serviceKey);

        return builder.HostBuilder.Services.AddKeyedConversationClient(
            serviceKey,
            services => CreateInnerVoiceLiveClient(services, builder, deploymentName));
    }

    public static LiveConversationClientBuilder AddKeyedConversationClient(this IHostApplicationBuilder builder, string connectionName)
    {
        var cs = builder.Configuration.GetConnectionString(connectionName);

        if (!ChatClientConnectionInfo.TryParse(cs, out var connectionInfo))
        {
            throw new InvalidOperationException($"Invalid connection string: {cs}. Expected format: 'Endpoint=endpoint;AccessKey=your_access_key;Model=model_name;Provider=ollama/openai/azureopenai/AzureVoiceLive;'.");
        }
        var liveConversationClientBuilder = connectionInfo.Provider switch
        {
            ClientChatProvider.OpenAI => builder.AddKeyedOpenAIClient(connectionName).AddKeyedConversationClient(connectionName, connectionInfo.SelectedModel),
            ClientChatProvider.AzureOpenAI => builder.AddKeyedAzureOpenAIClient(connectionName).AddKeyedConversationClient(connectionName, connectionInfo.SelectedModel),
            ClientChatProvider.AzureVoiceLive => builder.AddKeyedAzureVoiceLiveClient(connectionName).AddKeyedConversationVoiceLiveClient(connectionName, connectionInfo.SelectedModel),
            _ => throw new NotSupportedException($"Unsupported provider: {connectionInfo.Provider}")
        };
        // Add OpenTelemetry tracing for the ChatClient activity source
        liveConversationClientBuilder.UseOpenTelemetry();
        builder.Services.AddOpenTelemetry().WithTracing(t => t.AddSource("Experimental.Microsoft.Extensions.AI"));

        return liveConversationClientBuilder;
    }
    private static ILiveConversationClient CreateInnerVoiceLiveClient(
    IServiceProvider services,
    AspireOpenAIClientBuilder builder,
    string deploymentName)
    {
        var openAiClient = builder.ServiceKey is null
            ? services.GetRequiredService<VoiceLiveClient>()
            : services.GetRequiredKeyedService<VoiceLiveClient>(builder.ServiceKey);
        var loggerFactory = services.GetService<ILoggerFactory>();

        var conversationClient = new AzureVoiceLiveConversationClient(openAiClient, deploymentName, loggerFactory);
        if (builder.DisableTracing)
        {
            return conversationClient;
        }
        return new OpenTelemetryConversationClient(conversationClient, loggerFactory?.CreateLogger<OpenTelemetryConversationClient>());
    }
    private static ILiveConversationClient CreateInnerRealtimeClient(
        IServiceProvider services,
        AspireOpenAIClientBuilder builder,
        string deploymentName)
    {
        var openAiClient = builder.ServiceKey is null
            ? services.GetRequiredService<OpenAIClient>()
            : services.GetRequiredKeyedService<OpenAIClient>(builder.ServiceKey);
        var loggerFactory = services.GetService<ILoggerFactory>();

        var conversationClient = new Extensions.AI.RealtimeVoice.OpenAI.OpenAIRealtimeConversationClient(openAiClient, deploymentName, loggerFactory);
        if (builder.DisableTracing)
        {
            return conversationClient;
        }
        return new OpenTelemetryConversationClient(conversationClient, loggerFactory?.CreateLogger<OpenTelemetryConversationClient>());
    }
}
