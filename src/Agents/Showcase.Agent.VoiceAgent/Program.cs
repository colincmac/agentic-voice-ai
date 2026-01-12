using System.Configuration;
using A2A.AspNetCore;
using Agents.AI.Extensions.AgentAuthorization.AgentIdentity;
using Agents.AI.Extensions.AITools;
using Agents.AI.Hosting;
using Agents.AI.RealtimeVoice.Azure;
using Agents.AI.RealtimeVoice.Azure.Authorization.IdentityVerification;
using Agents.AI.RealtimeVoice.Azure.Calling;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Azure.Communication.CallAutomation;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.TokenCacheProviders.InMemory;
using Showcase.Agent.VoiceAgent;
using Showcase.Agent.VoiceAgent.Apis;
using Showcase.Agent.VoiceAgent.Configuration;
using Showcase.Agent.VoiceAgent.Teams;
using Showcase.Agent.VoiceAgent.Workflow;
using Showcase.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);
//builder.Services.AddGrpc();

builder.AddServiceDefaults();
builder.Services.AddHttpClient();

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

builder.AddRealtimeAIAgent(
    name: AgentConfig.TriageAgent,
    configurationSection: builder.Configuration.GetSection($"{AgentConfig.SectionName}:{AgentConfig.TriageAgent}"),
    liveConversationClientKey: "voicelive");

builder.AddAIAgent(
    name: "IvrOrchestrator",
    instructions: """
        You analyze voice conversation transcripts and determine when workflow step transitions should occur.
        Your decisions help guide the IVR workflow through greeting, intent collection, identity verification,
        and request handling phases.
        """,
    chatClientServiceKey: "chat");


builder.AddTestAgents();
builder.AddConversationHub(
    opt => builder.Configuration.GetSection(CommunicationOptions.SectionName).Bind(opt),
    opt =>
    {
        opt.RealtimeAgentServiceKey = AgentConfig.TriageAgent;
    })
    .AddCallAutomation()
    .AddToolCollection<TestTools>()
    .AddOperatorDashboard()
    .AddBiometricVoiceEvaluation()
    .AddStubCallAnalytics();
    // Add workflow integration with the orchestrator agent and workflow factory
    //.AddWorkflowIntegration(
    //    orchestratorAgentFactory: sp => sp.GetRequiredKeyedService<AIAgent>("IvrOrchestrator"),
    //    workflowFactory: ConversationWorkflowFactory.CreateCallerIntentWorkflow);

// TEAMS
builder.AddAgentApplicationOptions();

builder.AddAgent((sp) =>
{
    var chatAgent = sp.GetRequiredKeyedService<AIAgent>("pirate");
    var options = sp.GetRequiredService<AgentApplicationOptions>();
    return new TeamsAIAgent(options, chatAgent);
});
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

//app.UseHttpsRedirection();

//app.UseAuthentication();
//app.UseAuthorization();
//app.MapAgentIdentityManagement();

app.MapGet("/", () =>
{
    return "Testing";
});
app.MapWellKnownDidDocument();
app.MapTeams();


app.MapCallAutomation();
app.MapOperatorCalls();
app.MapOperatorDashboardHub();

// attach a2a with simple message communication
app.MapA2A(agentName: "pirate", path: "/a2a/pirate");
app.MapA2A(agentName: "knights-and-knaves", path: "/a2a/knights-and-knaves", agentCard: new()
{
    Name = "Knights and Knaves",
    Description = "An agent that helps you solve the knights and knaves puzzle.",
    Version = "1.0",

    // Url can be not set, and SDK will help assign it.
    // Url = "http://localhost:5390/a2a/knights-and-knaves"
});


//app.MapAgentDiscovery("/agents");
app.MapDefaultEndpoints();

app.Run();
