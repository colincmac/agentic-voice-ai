using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Security.Principal;
using Aspire.Hosting;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.CosmosDB;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Roles;
using Microsoft.Extensions.Configuration;
using Showcase.AppHost;
namespace Showcase.AppHost;

public static class DistributedApplicationBuilderExtensions
{
    public static IDistributedApplicationBuilder AddMonitoring(this IDistributedApplicationBuilder builder, IResourceBuilder<ParameterResource>? appInsightsName = null, IResourceBuilder<ParameterResource>? appInsightsResourceGroup = null)
    {

        var prometheus = builder.AddContainer("prometheus", "prom/prometheus", "v3.4.1")
            .WithBindMount("./prometheus", "/etc/prometheus", isReadOnly: true)
            .WithArgs("--web.enable-otlp-receiver", "--config.file=/etc/prometheus/prometheus.yml")
            .WithHttpEndpoint(targetPort: 9090)
            .WithUrlForEndpoint("http", u => u.DisplayText = "Prometheus Dashboard");

        var grafana = builder.AddContainer("grafana", "grafana/grafana")
            .WithBindMount("./grafana/config", "/etc/grafana", isReadOnly: true)
            .WithBindMount("./grafana/dashboards", "/var/lib/grafana/dashboards", isReadOnly: true)
            .WithEnvironment("PROMETHEUS_ENDPOINT", prometheus.GetEndpoint("http"))
            .WithHttpEndpoint(targetPort: 3000)
            .WithUrlForEndpoint("http", u => u.DisplayText = "Grafana Dashboard");

        var collector = builder.AddOpenTelemetryCollector("otelcollector").WithConfig("./otelcollector/config.yaml")
            .WithEnvironment("PROMETHEUS_ENDPOINT", $"{prometheus.GetEndpoint("http")}/api/v1/otlp")
            .WithAppForwarding();
        if (appInsightsName is not null)
        {
            var appinsights = builder.AddAzureApplicationInsights("appinsights").AsExisting(appInsightsName, appInsightsResourceGroup);
        }
        return builder;
    }

    public static IDistributedApplicationBuilder AddAzureResources(this IDistributedApplicationBuilder builder)
    {
        builder.AddAzureProvisioning();

        var devRedisPasswordParameter = builder.AddParameter("devRedisPassword", "hunter2", true).ExcludeFromManifest();

        #region Azure Resources
        var resourceGroupParam = builder.AddParameter(ParameterNameConstants.ResourceGroupName);
        var managedIdentityParam = builder.AddParameter(ParameterNameConstants.ManagedIdentityName);
        var cacheParam = builder.AddParameter(ParameterNameConstants.AzureCache);
        var appInsightsParam = builder.AddParameter(ParameterNameConstants.ApplicationInsights);

        var openAIChatModelParam = builder.AddParameter(ParameterNameConstants.OpenAIChatModel);
        var openAIChatModelVersionParam = builder.AddParameter(ParameterNameConstants.OpenAIChatModelVersion);

        var openAIRealtimeModelParam = builder.AddParameter(ParameterNameConstants.OpenAIRealtimeModel);
        var openAIRealtimeModelVersionParam = builder.AddParameter(ParameterNameConstants.OpenAIRealtimeModelVersion);

        var openAIParam = builder.AddParameter(ParameterNameConstants.OpenAIName);
        var openAIResourceGroupParam = builder.AddParameter(ParameterNameConstants.OpenAIResourceGroupName);

        var cosmosAccountParam = builder.AddParameter(ParameterNameConstants.CosmosAccount);
        var cosmosDbParam = builder.AddParameter(ParameterNameConstants.CosmosDatabase);


        //var sharedMi = builder.AddAzureUserAssignedIdentity(managedIdentityParam.Resource.GetValueAsync(CancellationToken.None).Result ?? string.Empty)
        //    .PublishAsExisting(managedIdentityParam, resourceGroupParam)
        //    .WithRoleAssignments(openai, CognitiveServicesBuiltInRole.AzureAIDeveloper);

#pragma warning disable ASPIRECOSMOSDB001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var cosmos = builder.AddAzureCosmosDB("cosmos")
            .AsExisting(cosmosAccountParam, resourceGroupParam)
            .RunAsPreviewEmulator(cosmosEmulator =>
            {
                cosmosEmulator.WithDataExplorer();
                cosmosEmulator.WithDataVolume("cosmosdb");
            });
#pragma warning restore ASPIRECOSMOSDB001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var cosmosDatabase = cosmos.AddCosmosDatabase("cosmosdb", cosmosDbParam.Resource.GetValueAsync(CancellationToken.None).Result);

        var aiConversationContainer = cosmosDatabase.AddContainer("conversations", "/partitionKey", "conversations");

        var userProfileContainer = cosmosDatabase.AddContainer("userProfiles", "/partitionKey", "userProfiles");

        var referenceDataContainer = cosmosDatabase.AddContainer("referenceData", "/partitionKey", "referenceData");

        var teamsStateContainer = cosmosDatabase.AddContainer("msteamsState", "/id", "msteamsState");

        var actorStateContainer = cosmosDatabase.AddContainer("agentState", ["/actorId", "/key"], "agentState");


        var acaEnv = builder.AddAzureContainerAppEnvironment("aca-env");

        #endregion



        return builder;
    }


 }
