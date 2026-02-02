#pragma warning disable ASPIREAZURE001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.CosmosDB;
using Showcase.AppHost;

var builder = DistributedApplication.CreateBuilder(args);


var defaultResourceGroupParam = builder.AddParameter(ParameterNameConstants.ResourceGroupName);

var sharedMi = builder.AddAzureUserAssignedIdentity(ParameterNameConstants.ManagedIdentityName);
var appInsightsNameParam = builder.AddParameter(ParameterNameConstants.ApplicationInsights);
var lawParam = builder.AddParameter(ParameterNameConstants.LogAnalyticsWorkspace);
var lawRgParam = builder.AddParameter(ParameterNameConstants.LogAnalyticsWorkspaceRg);

var containerAppParam = builder.AddParameter(ParameterNameConstants.ContainerAppEnvironment);
var containerRegistryParam = builder.AddParameter(ParameterNameConstants.ContainerRegistry);
var keyVaultParam = builder.AddParameter(ParameterNameConstants.KeyVault);
var appConfigParam = builder.AddParameter(ParameterNameConstants.AppConfig);

var redisParam = builder.AddParameter(ParameterNameConstants.AzureCache);
var cosmosAccountParam = builder.AddParameter(ParameterNameConstants.CosmosAccount);



//var openAIResourceGroupParam = builder.AddParameter(ParameterNameConstants.OpenAIResourceGroupName);
//var openAIChatModelParam = builder.AddParameter(ParameterNameConstants.OpenAIChatModel);
//var openAIChatModelVersionParam = builder.AddParameter(ParameterNameConstants.OpenAIChatModelVersion);

//var openAIRealtimeModelParam = builder.AddParameter(ParameterNameConstants.OpenAIRealtimeModel);
//var openAIRealtimeModelVersionParam = builder.AddParameter(ParameterNameConstants.OpenAIRealtimeModelVersion);

//var openAIParam = builder.AddParameter(ParameterNameConstants.OpenAIName);

//var embeddingModelParam = builder.AddParameter(ParameterNameConstants.OpenAIEmbeddingModel);
//var embeddingModelVersionParam = builder.AddParameter(ParameterNameConstants.OpenAIEmbeddingModelVersion);


/**
 * Monitoring & Telemetry
 */
var appinsights = builder.AddAzureApplicationInsights("appinsights")
    .AsExisting(appInsightsNameParam, defaultResourceGroupParam);

//var law = builder.AddAzureLogAnalyticsWorkspace("law")
//    .AsExisting(lawParam, lawRgParam);

/**
 * Compute Environments
 */

var registry = builder.AddAzureContainerRegistry("acr")
    .AsExisting(containerRegistryParam, defaultResourceGroupParam);


var acaEnvironment = builder.AddAzureContainerAppEnvironment("aca-env")
    .AsExisting(containerAppParam, defaultResourceGroupParam)
    .WithDashboard()
    .ConfigureInfrastructure(config =>
    {
        var resources = config.GetProvisionableResources();
        var containerEnvironment = resources.OfType<ContainerAppManagedEnvironment>().FirstOrDefault();
        //containerEnvironment?.AppLogsConfiguration = new ContainerAppLogsConfiguration();


    });

/**
 * OpenAI Deployments
 */
var embedding = builder.AddConnectionString("embedding"); //openai.AddDeployment(name: "embedding", modelName: "text-embedding-3-large", modelVersion: "1");
var chat = builder.AddConnectionString("chat");//openai.AddDeployment(name: "chat", modelName: "gpt-5.1-chat", modelVersion: "2025-11-13");
var realtime = builder.AddConnectionString("realtime");//openai.AddDeployment(name: "realtime", modelName: "gpt-realtime", modelVersion: "2025-06-03");
var voicelive = builder.AddConnectionString("voicelive");//openai.AddDeployment(name: "voicelive", modelName: "gpt-realtime", modelVersion: "2025-06-03");

/**
 * Database and Storage Resources
 */

var cosmos = builder.AddAzureCosmosDB("cosmosdb")
    .AsExisting(cosmosAccountParam, defaultResourceGroupParam)
    .PublishAsConnectionString()
    .RunAsPreviewEmulator(emulator =>
    {
        emulator.WithDataVolume();
        emulator.WithDataExplorer();
    });

var voiceAgentDb = cosmos.AddCosmosDatabase("ContactCenter", "ContactCenter");

var temp2 = builder.AddParameter("redistemprg");
var temp1 = builder.AddParameter("redistemp");
var redis = builder.AddAzureManagedRedis("redis")
    .AsExisting(temp1, temp2).PublishAsConnectionString();

//var appConfig =
//    builder.AddAzureAppConfiguration("appconfig")
//    .AsExisting(appConfigParam, defaultResourceGroupParam).ExcludeFromManifest();

var keyVault = builder.AddAzureKeyVault("secrets")
    .AsExisting(keyVaultParam, defaultResourceGroupParam);


//builder.AddMonitoring(appInsightsName: appInsightsNameParam);

#region Projects

//var biometricsApi = builder.AddPythonApp(
//    name: "python-biometrics-grpc-api",
//    appDirectory: "../../python-services/voice-biometrics",
//    scriptPath: "server.py")
//    .WithHttpEndpoint(targetPort: 51001, name: "grpc")
//    .WithHttpEndpoint(targetPort: 51002, name: "http")
//    .WithEnvironment("HTTP_HEALTH_PORT", "51002")
//    .WithEnvironment("GRPC_PORT", "51002")
//    .WithReference(cosmos)
//    .WithReference(voiceAgentDb)
//    .WithReference(appinsights)
//    .WithReference(keyVault)
//    .WithReference(appConfig)
//    .WithComputeEnvironment(acaEnvironment)
//    .WithUv();

var voiceAgent = builder.AddProject<Projects.Showcase_Agent_VoiceAgent>("voiceagent")
    // API References
    //.WithReference(biometricsApi)

    // AI References 
    .WithReference(chat)
    .WithReference(embedding)
    .WithReference(realtime)
    .WithReference(voicelive)
    // Azure Resources
    .WithReference(voiceAgentDb, "cosmos")
    .WithReference(appinsights)
    .WithReference(keyVault)
    //.WithReference(appConfig)
    .WithReference(redis)
    //.WithAzureUserAssignedIdentity(sharedMi)
    //.WithEnvironment("CONNECTIONSTRINGS__voicebiometrics", $"{biometricsApi.GetEndpoint("grpc")}")
    .WithExternalHttpEndpoints();

    //.WaitFor(biometricsApi);


#endregion

builder.Build().Run();



record AddCosmosDataRoleAssignmentsContext(AzureResourceInfrastructure Infrastructure, BicepValue<Guid> PrincipalId) : IAddRoleAssignmentsContext
{
    public AzureResourceInfrastructure Infrastructure { get; } = Infrastructure;

    public IEnumerable<RoleDefinition> Roles => throw new NotImplementedException();

    public BicepValue<RoleManagementPrincipalType> PrincipalType => throw new NotImplementedException();

    public BicepValue<Guid> PrincipalId { get; } = PrincipalId;

    public BicepValue<string> PrincipalName => throw new NotImplementedException();

    public DistributedApplicationExecutionContext ExecutionContext => throw new NotImplementedException();
}
internal class CosmosDBSqlRoleDefinition_Derived : CosmosDBSqlRoleDefinition
{
    private BicepValue<string>? _nameOverride;

    public CosmosDBSqlRoleDefinition_Derived(string name) : base(name)
    {
    }

    public static CosmosDBSqlRoleDefinition_Derived FromExisting(string bicepIdentifier)
    {
        return new CosmosDBSqlRoleDefinition_Derived(bicepIdentifier)
        {
            IsExistingResource = true
        };
    }

    public BicepValue<string> NameOverride
    {
        get
        {
            Initialize();
            return _nameOverride!;
        }
        set
        {
            Initialize();
            _nameOverride!.Assign(value);
        }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();

        _nameOverride = DefineProperty<string>("Name", new string[1] { "name" });
    }
}

internal class CosmosDBSqlRoleAssignment_Derived : CosmosDBSqlRoleAssignment
{
    private BicepValue<string>? _nameOverride;

    public CosmosDBSqlRoleAssignment_Derived(string name) : base(name)
    {
    }

    public BicepValue<string> NameOverride
    {
        get
        {
            Initialize();
            return _nameOverride!;
        }
        set
        {
            Initialize();
            _nameOverride!.Assign(value);
        }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();

        _nameOverride = DefineProperty<string>("Name", new string[1] { "name" });
    }
}
