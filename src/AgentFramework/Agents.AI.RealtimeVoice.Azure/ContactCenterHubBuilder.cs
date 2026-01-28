using System.Threading.Channels;
using Agents.AI.Extensions.AITools;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.Extensions.ToolApproval;
using Agents.AI.RealtimeVoice.Azure.Authorization.Biometrics;
using Agents.AI.RealtimeVoice.Azure.Authorization.FraudCheck;
using Agents.AI.RealtimeVoice.Azure.Authorization.IdentityVerification;
using Agents.AI.RealtimeVoice.Azure.Biometrics.Grpc;
using Agents.AI.RealtimeVoice.Azure.CallAutomation;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Agents.AI.RealtimeVoice.Azure.Monitoring;
using Azure.Communication.CallAutomation;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Models;

namespace Agents.AI.RealtimeVoice.Azure.Calling;


public class ConversationHubBuilder
{

    public IHostApplicationBuilder HostBuilder { get; private set; }

    internal ConversationHubBuilder(IHostApplicationBuilder builder)
    {
        HostBuilder = builder;
    }

    /// <summary>
    /// Adds voice biometric evaluation services using the external biometrics gRPC API.
    /// This connects to a real biometrics service for voice enrollment and verification.
    /// </summary>
    /// <param name="configure">Optional configuration action for API options.</param>
    /// <param name="configurationSectionName">Configuration section name. Default is "BiometricsApi".</param>
    /// <returns>The builder for chaining.</returns>
    public ConversationHubBuilder AddBiometricVoiceEvaluation(
        Action<VoiceBiometricsApiOptions>? configure = null,
        string configurationSectionName = VoiceBiometricsApiOptions.SectionName)
    {
        var section = HostBuilder.Configuration.GetRequiredSection(configurationSectionName);
        HostBuilder.Services.AddOptions<VoiceBiometricsApiOptions>().Bind(section)
            .Configure<IConfiguration>((opt, config) =>
        {
            configure?.Invoke(opt);
            if (string.IsNullOrEmpty(opt.Endpoint) && !string.IsNullOrEmpty(opt.ConnectionStringName))
            {
                var connectionString = config.GetConnectionString(opt.ConnectionStringName);
                if(!string.IsNullOrEmpty(connectionString))
                {
                    opt.Endpoint = connectionString;
                }
            }
        });

        HostBuilder.Services.AddGrpcClient<BiometricService.BiometricServiceClient>((sp, configure) =>
        {
            var options = sp.GetRequiredService<IOptions<VoiceBiometricsApiOptions>>().Value;
            
            if(string.IsNullOrEmpty(options.Endpoint))
            {
                throw new InvalidOperationException("Biometrics API endpoint is not configured. Please provide either a valid endpoint or a connection string.");
            }
            configure.Address = new Uri(options.Endpoint);
        })
            .ConfigureChannel((sp, channelOptions) =>
        {
            var providedOptions = sp.GetRequiredService<IOptions<VoiceBiometricsApiOptions>>().Value;
            if(providedOptions.AllowInsecureConnection)
            {
                // Allow plaintext HTTP/2 connections for development environments
                var handler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true
                };
                channelOptions.HttpHandler = handler;
            }

        });

        // Register the API evaluator
        HostBuilder.Services.AddScoped<IAIToolCollection, VoiceBiometricTools>();
        HostBuilder.Services.AddScoped<IVoiceBiometricEvaluator, ApiBiometricEvaluator>();
        HostBuilder.Services.AddScoped<IToolApprovalHandler, VoiceBiometricHandler>();

        // Add telemetry for the API client
        HostBuilder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(ApiBiometricEvaluator.ActivitySource.Name));

        return this;
    }

    /// <summary>
    /// Adds identity verification services using Decentralized Identifiers (DIDs). Does not map DID endpoints, but registers services for using <see cref="DidEndpointBuilderExtensions.MapWellKnownDidDocument(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, string)"/>
    /// </summary>
    /// <param name="configure"></param>
    /// <param name="configurationSectionName"></param>
    /// <returns></returns>
    public ConversationHubBuilder AddIdentityVerification(Action<DidOptions>? configure = null, string configurationSectionName = DidOptions.ConfigurationSectionName)
    {
        HostBuilder.AddDecentralizedIDOptions(configure, configurationSectionName);
        HostBuilder.Services.AddSingleton<IIdentityVerificationService, EntraIdentityVerificationService>();
        HostBuilder.Services.AddScoped<IToolApprovalHandler, IdentityVerificationHandler>();
        return this;
    }

    /// <summary>
    /// Still need to map the CallAutomationAPI endpoints in your app. 
    /// </summary>
    /// <param name="configureTeamsConnection"></param>
    /// <returns></returns>
    public ConversationHubBuilder AddCallAutomation(bool configureTeamsConnection = false)
    {
        HostBuilder.Services.AddSingleton((sp) =>
        {
            var callOptions = sp.GetRequiredService<IOptions<CommunicationOptions>>();
            return new CallAutomationClient(callOptions.Value.Acs.ConnectionString);
        });
        HostBuilder.Services.AddSingleton<AzureCommunicationService>();

        if (configureTeamsConnection)
        {
            HostBuilder.Services.AddHostedService<AzureTeamsConfigurationStartupService>();
        }

        return this;
    }

    public ConversationHubBuilder AddFraudDetection<TFraudMonitor>(Func<IServiceProvider, TFraudMonitor>? factory = null) where TFraudMonitor : class, IFraudDetectionMonitor
    {

        if (factory != null)
        {
            HostBuilder.Services.AddScoped<IFraudDetectionMonitor>(factory);
        }
        else
        {
            HostBuilder.Services.AddScoped<IFraudDetectionMonitor, FraudDetectionMonitor>();
        }

        HostBuilder.Services.AddScoped<IToolApprovalHandler, FraudCheckHandler>();
        return this;
    }

    public ConversationHubBuilder AddToolCollection<TToolCollection>(Func<IServiceProvider, TToolCollection>? factory = null) where TToolCollection : class, IAIToolCollection
    {
        if(factory == null)
        {
            HostBuilder.Services.AddScoped<IAIToolCollection, TToolCollection>();
        }
        else
        {
            HostBuilder.Services.AddScoped<IAIToolCollection>(factory);
        }
        return this;
    }

    /// <summary>
    /// Adds the operator dashboard SignalR hub and broadcaster for real-time call monitoring.
    /// Call <see cref="OperatorDashboardEndpointBuilderExtensions.MapOperatorDashboardHub"/> to map the SignalR endpoint.
    /// </summary>
    /// <returns>The builder for chaining.</returns>
    public ConversationHubBuilder AddOperatorDashboard()
    {
        HostBuilder.Services.AddSignalR();
        HostBuilder.Services.AddHostedService<OperatorDashboardBroadcaster>();
        HostBuilder.Services.AddGrpc();

        return this;
    }

    /// <summary>
    /// Adds the call analytics service for real-time health monitoring.
    /// </summary>
    /// <typeparam name="TAnalyticsService">Custom analytics service type.</typeparam>
    /// <param name="factory">Optional factory for creating the analytics service.</param>
    /// <returns>The builder for chaining.</returns>
    public ConversationHubBuilder AddCallAnalytics<TAnalyticsService>(Func<IServiceProvider, TAnalyticsService>? factory = null)
        where TAnalyticsService : class, ICallAnalyticsService
    {
        if (factory is null)
        {
            HostBuilder.Services.AddSingleton<ICallAnalyticsService, TAnalyticsService>();
        }
        else
        {
            HostBuilder.Services.AddSingleton<ICallAnalyticsService>(factory);
        }

        return this;
    }

        /// <summary>
        /// Adds the stub call analytics service using simple keyword-based analysis.
        /// For production, use <see cref="AddCallAnalytics{TAnalyticsService}"/> with a real implementation.
        /// </summary>
        /// <returns>The builder for chaining.</returns>
        public ConversationHubBuilder AddStubCallAnalytics()
        {
            HostBuilder.Services.AddSingleton<ICallAnalyticsService, StubCallAnalyticsService>();
            return this;
        }

        /// <summary>
        /// Adds workflow-integrated session activation for IVR workflows.
        /// This registers the <see cref="WorkflowIntegratedSessionActivator"/> which coordinates
        /// the realtime AI agent with workflow step progression.
        /// </summary>
        /// <param name="orchestratorAgentFactory">
        /// Factory for creating the orchestrator chat client agent that analyzes turns and makes workflow decisions.
        /// </param>
        /// <param name="workflowFactory">
        /// Factory for creating the workflow definition for each session. Receives the session ID.
        /// </param>
        /// <returns>The builder for chaining.</returns>
        /// <remarks>
        /// <para>
        /// The workflow-integrated activator ensures:
        /// <list type="bullet">
        /// <item>The AI agent only has access to tools appropriate for the current step</item>
        /// <item>The agent's behavior (via prompts) changes as the workflow progresses</item>
        /// <item>Transitions between steps are gated by guards (e.g., authentication level)</item>
        /// </list>
        /// </para>
        /// </remarks>
        /// <example>
        /// <code>
        /// builder.AddConversationHub()
        ///     .AddWorkflowIntegration(
        ///         sp => CreateOrchestratorAgent(sp),
        ///         sessionId => CreateWorkflowDefinition(sessionId));
        /// </code>
        /// </example>
        //public ConversationHubBuilder AddWorkflowIntegration(
        //    Func<IServiceProvider, AIAgent> orchestratorAgentFactory,
        //    Func<string, RealtimeIvrWorkflowDefinition> workflowFactory)
        //{
        //    // Register the coordinator factory
        //    HostBuilder.Services.AddSingleton<IRealtimeIvrWorkflowCoordinatorFactory>(sp =>
        //    {
        //        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        //        return new RealtimeIvrWorkflowCoordinatorFactory(
        //            () => orchestratorAgentFactory(sp),
        //            loggerFactory);
        //    });

        //    // Replace the default session activator with the workflow-integrated one
        //    HostBuilder.Services.RemoveAll<IContactCenterConversationSessionActivator>();
        //    HostBuilder.Services.AddSingleton<IContactCenterConversationSessionActivator>(sp =>
        //    {
        //        var coordinatorFactory = sp.GetRequiredService<IRealtimeIvrWorkflowCoordinatorFactory>();
        //        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

        //        return new WorkflowIntegratedSessionActivator(
        //            coordinatorFactory,
        //            scopedSp => scopedSp.GetRequiredService<AuthorizingRealtimeAIAgent>(),
        //            workflowFactory,
        //            loggerFactory);
        //    });

        //    return this;
        //}

        /// <summary>
        /// Adds workflow-integrated session activation using a keyed orchestrator agent.
        /// </summary>
        /// <param name="orchestratorAgentKey">
        /// The service key for the orchestrator <see cref="ChatClientAgent"/>.
        /// </param>
        /// <param name="workflowFactory">
        /// Factory for creating the workflow definition for each session.
        /// </param>
        /// <returns>The builder for chaining.</returns>
        //public ConversationHubBuilder AddWorkflowIntegration(
        //    string orchestratorAgentKey,
        //    Func<string, RealtimeIvrWorkflowDefinition> workflowFactory)
        //{
        //    return AddWorkflowIntegration(
        //        sp => sp.GetRequiredKeyedService<ChatClientAgent>(orchestratorAgentKey),
        //        workflowFactory);
        //}

    }
