#pragma warning disable ASPIREAZURE001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.CosmosDB;
using Showcase.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

var resourceGroupParam = builder.AddParameter(ParameterNameConstants.ResourceGroupName);

var appInsightsNameParam = builder.AddParameter(ParameterNameConstants.ApplicationInsights);
var cosmosAccountParam = builder.AddParameter(ParameterNameConstants.CosmosAccount);

var foundryResourceParam = builder.AddParameter(ParameterNameConstants.FoundryResourceName);

var openAIChatModelParam = builder.AddParameter(ParameterNameConstants.OpenAIChatModel);
var openAIChatModelVersionParam = builder.AddParameter(ParameterNameConstants.OpenAIChatModelVersion);

var openAIRealtimeModelParam = builder.AddParameter(ParameterNameConstants.OpenAIRealtimeModel);
var openAIRealtimeModelVersionParam = builder.AddParameter(ParameterNameConstants.OpenAIRealtimeModelVersion);

var openAIParam = builder.AddParameter(ParameterNameConstants.OpenAIName);
var openAIResourceGroupParam = builder.AddParameter(ParameterNameConstants.OpenAIResourceGroupName);

var embeddingModelParam = builder.AddParameter(ParameterNameConstants.OpenAIEmbeddingModel);
var embeddingModelVersionParam = builder.AddParameter(ParameterNameConstants.OpenAIEmbeddingModelVersion);

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

#pragma warning disable ASPIRECOSMOSDB001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
var cosmos = builder.AddAzureCosmosDB("cosmosdb")
    .AsExisting(cosmosAccountParam, resourceGroupParam)
    .RunAsPreviewEmulator(emulator =>
    {
        emulator.WithDataVolume();
        emulator.WithDataExplorer();

    });
#pragma warning restore ASPIRECOSMOSDB001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

var familyAiDb = cosmos.AddCosmosDatabase("FamilyHistory");
var voiceAgentDb = cosmos.AddCosmosDatabase("ContactCenter");

/**
 * Monitoring & Telemetry
 */
builder.AddMonitoring(appInsightsName: appInsightsNameParam);


#region Projects

var biometricsApi = builder.AddPythonApp(
    name: "python-biometrics-grpc-api",
    appDirectory: "../../python-services/voice-biometrics",
    scriptPath: "server.py")
    .WithHttpEndpoint(targetPort: 50051, name: "grpc")
    .WithHttpEndpoint(targetPort: 8090, name: "http")
    .WithEnvironment("HTTP_HEALTH_PORT", "8090")
    .WithUv();

var voiceAgent = builder.AddProject<Projects.Showcase_Agent_VoiceAgent>("voiceagent")
    .WithReference(chat)
    .WithReference(embedding)
    .WithReference(realtime)
    .WithReference(voicelive)
    .WithReference(voiceAgentDb, "cosmos")
    .WithReference(biometricsApi)
    .WithEnvironment("CONNECTIONSTRINGS__voicebiometrics", $"{biometricsApi.GetEndpoint("grpc")}")
    .WaitFor(biometricsApi);


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
