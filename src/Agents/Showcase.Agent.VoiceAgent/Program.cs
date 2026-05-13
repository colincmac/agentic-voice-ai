using System.Collections.Concurrent;
using A2A.AspNetCore;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.LiveVoice.Media.Audio;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Agents.AI.Hosting;
using Agents.AI.Realtime;
using Agents.AI.RealtimeVoice.Azure;
using Agents.AI.RealtimeVoice.Azure.Authorization.IdentityVerification;
using Agents.AI.RealtimeVoice.Azure.Calling;
using Agents.AI.RealtimeVoice.Azure.Calling.Proposed;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Azure.Identity;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Azure;
using OpenTelemetry.Resources;
using Showcase.Agent.VoiceAgent;
using Showcase.Agent.VoiceAgent.Apis;
using Showcase.Agent.VoiceAgent.Configuration;
using Showcase.Agent.VoiceAgent.Teams;
using Showcase.Agent.VoiceAgent.Workflow;
using Showcase.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);
//builder.Services.AddGrpc();
var azureSection = builder.Configuration.GetSection("Azure");
var tenantId = azureSection["TenantId"];

var credential = new AzureCliCredential();
builder.Services.AddAzureClients(clientBuilder =>
{
    // Make this the default for clients created by the factory
    clientBuilder.UseCredential(credential);
});

if (builder.Environment.IsDevelopment())
{
    var resourceAttributes = new Dictionary<string, object> {
    { "service.name", "artagent" },
    { "service.namespace", "dev" },
    { "service.instance.id", "local" }};

    builder.AddServiceDefaults(opt => opt.AddAttributes(resourceAttributes));
}
else
{
    builder.AddServiceDefaults();
}
builder.Services.AddHttpClient();
builder.Services.AddHttpLogging(o => { });
//builder.Services.AddAzureClients(clientBuilder =>
//{
//    // Set up any default settings
//    clientBuilder.ConfigureDefaults(
//        builder.Configuration.GetSection("AzureDefaults"));
//});

// Retrieve the endpoint
var appConfigEndpoint = builder.Configuration.GetConnectionString("appconfig");

if (!string.IsNullOrWhiteSpace(appConfigEndpoint))
{
    builder.Configuration.AddAzureAppConfiguration(appConfigEndpoint);
}

//builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
//    .AddMicrosoftIdentityWebApi(builder.Configuration)
//    .EnableTokenAcquisitionToCallDownstreamApi();

//builder.AddAgentIdentityManagement();

//builder.Services.AddAgentIdentities();
//builder.Services.AddInMemoryTokenCaches();
//builder.AddDecentralizedIDOptions();


// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
//builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

// Add services to the container.


// END ACS
builder.AddKeyedChatClient("chat")
    .UseFunctionInvocation()
    .UseOpenTelemetry(sourceName: "Showcase.VoiceAgent");

builder.AddKeyedConversationClient("realtime")
    .UseFunctionInvocation()
    .UseOpenTelemetry(sourceName: "Showcase.VoiceAgent");

builder.AddKeyedConversationClient("voicelive")
    .UseFunctionInvocation()
    .UseOpenTelemetry(sourceName: "Showcase.VoiceAgent");

// New Calling/Proposed shape: registers ICallSessionFactory + ICallSessionRegistry +
// ICallQualityReporter, and wires the realtime voice strategy on top of the existing
// AuthorizingRealtimeAIAgent. ISpeechSynthesizer would be added separately to enable DTMF.
builder.Services.Configure<CommunicationOptions>(builder.Configuration.GetSection(CommunicationOptions.SectionName));

builder.Services.AddSingleton<ISpeechSynthesizer, AzureSpeechSynthesizer>(sp =>
{
    var endpoint = builder.Configuration.GetConnectionString("azurespeech");
    if(string.IsNullOrEmpty(endpoint)) throw new InvalidOperationException("Azure Speech endpoint is not configured.");

    return new AzureSpeechSynthesizer(new Uri(endpoint));
});
var callerIntentWorkflow = ConversationWorkflowFactory.CreateCallerIntentWorkflow(sessionId: "default");
var dtmfWorkflow = ConversationWorkflowFactory.CreateDtmfWorkflow(sessionId: "default");

var dtmf2 = new RealtimeIvrWorkflowDefinition()
{
    Name = "test-ivr",
    BasePrompt = new RealtimePrompt(),
    Steps =
        [
            new RealtimeIvrWorkflowStep
            {
                Id = "language",
                ConversationState = new ConversationState
                {
                    Id = "language",
                    Description = "Welcome to Contoso",
                    Goal = "Route the caller",
                    Instructions = ["Greet the caller and offer menu"],
                    Transitions =
                    [
                        new StateTransition { NextStep = "main_menu", Condition = "selected language" }
                    ]
                },
                StepDtmfConfiguration = new StepDtmfConfiguration(maxNumberOfDigits: 1)
                {
                    MenuOptions = new Dictionary<char, DtmfMenuOption>
                    {
                        ['1'] = new() { Digit = '1', Label = "english", NextStepId = "english" },
                        ['2'] = new() { Digit = '2', Label = "spanish", NextStepId = "spanish" },
                    }
                }
            },
            new RealtimeIvrWorkflowStep
            {
                Id = "main_menu",
                ConversationState = new ConversationState
                {
                    Id = "main_menu",
                    Description = "Main menu",
                    Instructions = ["Greet the caller and offer menu options"]
                }
            }
        ]
};
builder.Services.AddSingleton<RealtimeIvrWorkflowDefinition>(sp => callerIntentWorkflow);

// The realtime agent that the new realtime backend wraps. Reads its config from
// Agents:TriageAgent and uses the "voicelive" conversation client registered above.
builder.AddRealtimeAIAgent(
    name: AgentConfig.TriageAgent,
    configurationSection: builder.Configuration.GetSection($"{AgentConfig.SectionName}:{AgentConfig.TriageAgent}"),
    liveConversationClientKey: "voicelive");

builder.AddCallSessionContainer()
    .AddAcsCallAutomation()
    .AddRealtimeVoiceStrategy(realtimeAgentServiceKey: AgentConfig.TriageAgent)
    .AddCallControlTools()
    .AddDashboardProjectionObserver();

// TEAMS
builder.AddAgentApplicationOptions();

// builder.AddAgent((sp) =>
// {
//     var chatAgent = sp.GetRequiredKeyedService<AIAgent>("pirate");
//     var options = sp.GetRequiredService<AgentApplicationOptions>();
//     return new TeamsAIAgent(options, chatAgent);
// });
builder.Services.AddSingleton<IStorage, MemoryStorage>();

// End TEAMS
var app = builder.Build();

app.UseRouting();
app.UseWebSockets();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
app.UseHttpLogging();

//app.UseHttpsRedirection();

//app.UseAuthentication();
//app.UseAuthorization();
//app.MapAgentIdentityManagement();

app.MapGet("/", async ([FromServices] AuthorizingRealtimeAIAgent agent, CancellationToken cancellationToken) =>
{
    var session = await agent.CreateRealtimeSessionAsync(null, cancellationToken);
    return "Testing";
});
app.MapWellKnownDidDocument();
app.MapTeams();


app.MapCallAutomation();
app.MapOperatorCalls();
// app.MapOperatorDashboardHub();

//app.MapAgentDiscovery("/agents");
app.MapDefaultEndpoints();

app.Run();


