using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.DependencyInjection;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.ContactCenter.Azure;
using Agents.AI.Hosting;
using Agents.AI.ContactCenter.Authorization.IdentityVerification;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Configuration;
using Agents.Intent.V1;
using Azure.Identity;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Azure;
using OpenTelemetry.Resources;
using Showcase.Agent.VoiceAgent;
using Showcase.Agent.VoiceAgent.Apis;
using Showcase.Agent.VoiceAgent.Authentication;
using Showcase.Agent.VoiceAgent.Configuration;
using Showcase.Agent.VoiceAgent.Nlu;
using Showcase.Agent.VoiceAgent.Workflow;
using Showcase.ServiceDefaults;
using Agents.AI.ContactCenter.DependencyInjection;
using Agents.AI.ContactCenter.Media.Analysis;

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

var azureSpeechConnectionString = builder.Configuration.GetConnectionString("AzureSpeech");

builder.Services.AddAzureSpeech(options =>
{
    builder.Configuration.GetSection("AzureSpeech").Bind(options);

    if (!string.IsNullOrWhiteSpace(azureSpeechConnectionString))
    {
        options.Endpoint = new Uri(azureSpeechConnectionString);
    }
});

// E2E showcase: register the auth-aware DTMF workflow as the default and the realtime
// equivalent under a tier-keyed slot, so the CallingApi can pick either via ?tier=.
builder.Services.AddSingleton<InMemoryCallerDirectory>();
builder.Services.AddSingleton<ICallerDirectory>(sp => sp.GetRequiredService<InMemoryCallerDirectory>());
builder.Services.AddSingleton<CallerAuthStateRegistry>();

// Declarative YAML IVR framework: loads workflow definitions from
// Workflow\Samples\*.yaml (copied to the app output via the csproj content glob),
// compiles them into RealtimeIvrWorkflowDefinition instances, and exposes the
// caller-auth tools (`pin-validator`, `confirm-identity`, `transfer-to-agent`) under
// names the YAML samples reference. Showcase predicates and additional sources can
// be appended here.
builder.Services.AddIvrWorkflowFramework(b => b
    .AddFileSystemSource(Path.Combine(AppContext.BaseDirectory, ShowcaseWorkflowIds.SamplesDirectory))
    .AddTool("pin-validator", sp => PinValidationTools.ValidatePinTool(
        sp.GetRequiredService<InMemoryCallerDirectory>(),
        sp.GetRequiredService<ILoggerFactory>()))
    .AddTool("confirm-identity", sp => PinValidationTools.ConfirmIdentityTool(
        sp.GetRequiredService<InMemoryCallerDirectory>(),
        sp.GetRequiredService<ILoggerFactory>()))
    .AddTool("transfer-to-agent", _ => TransferTools.BuildTransferToAgentTool(
        ShowcaseWorkflowIds.DefaultEscalationNumber)));

// Demo NLU dependencies — keyword classifier + scripted recognizer keep the showcase
// free of an Azure CLU / Speech-to-Text dependency while still exercising the NLU
// strategy end-to-end. When the AppHost wires an `intentagent` resource (the GPU
// gRPC service in the two-pool AKS topology — see docs/architecture/aks-topology.md),
// the voice-edge talks to it over gRPC; otherwise the in-process stub keeps the
// showcase self-contained.
var intentAgentEndpoint = builder.Configuration.GetConnectionString("intentagent")
    ?? builder.Configuration["services:intentagent:grpc:0"]
    ?? builder.Configuration["services:intentagent:https:0"]
    ?? builder.Configuration["services:intentagent:http:0"];

if (!string.IsNullOrWhiteSpace(intentAgentEndpoint))
{
    builder.Services.AddGrpcClient<IntentClassification.IntentClassificationClient>(options =>
    {
        options.Address = new Uri(intentAgentEndpoint);
    });
    builder.Services.AddSingleton<IIntentClassifier, GrpcIntentClassifier>();
}
else
{
    builder.Services.AddSingleton<IIntentClassifier, StubKeywordIntentClassifier>();
}

builder.Services.AddTransient<ISpeechRecognizer, StubSpeechRecognizer>();

// Workflow definitions are now loaded from the YAML samples under
// Workflow\Samples\ via IIvrWorkflowLoader (registered by AddIvrWorkflowFramework
// above). The default registration is the authenticated DTMF flow; the keyed
// registrations let the CallingApi pick a tier-specific workflow via ?tier=.
builder.Services.AddSingleton<RealtimeIvrWorkflowDefinition>(sp =>
    ShowcaseWorkflowLoader.Load(sp, ShowcaseWorkflowIds.AuthenticatedDtmf));

builder.Services.AddKeyedSingleton<RealtimeIvrWorkflowDefinition>(
    nameof(AgentTier.DtmfOnly),
    (sp, _) => ShowcaseWorkflowLoader.Load(sp, ShowcaseWorkflowIds.AuthenticatedDtmf));

builder.Services.AddKeyedSingleton<RealtimeIvrWorkflowDefinition>(
    nameof(AgentTier.RealtimeVoice),
    (sp, _) => ShowcaseWorkflowLoader.Load(sp, ShowcaseWorkflowIds.AuthenticatedRealtime));

builder.Services.AddKeyedSingleton<RealtimeIvrWorkflowDefinition>(
    nameof(AgentTier.IntentNlu),
    (sp, _) => ShowcaseWorkflowLoader.Load(sp, ShowcaseWorkflowIds.AuthenticatedRealtime));

// The realtime agent that the new realtime backend wraps. Reads its config from
// Agents:TriageAgent and uses the "voicelive" conversation client registered above.
builder.AddRealtimeAIAgent(
    name: AgentConfig.TriageAgent,
    configurationSection: builder.Configuration.GetSection($"{AgentConfig.SectionName}:{AgentConfig.TriageAgent}"),
    liveConversationClientKey: "voicelive");

builder.AddCallSessionContainer()
    // Inner factories — the composite below shadows the top tier and reuses these
    // through DI. Order matters: register the inner tiers BEFORE the composite so
    // the composite's lookup finds them.
    .AddRealtimeVoiceStrategy(realtimeAgentServiceKey: AgentConfig.TriageAgent)
    .AddNluStrategy()
    .AddDtmfStrategy()
    .AddCallControlTools()
    // Caller authentication: ANI lookup against the in-memory directory plus the
    // anonymous fallback so unknown callers still walk the workflow as guests.
    .AddCallerAuthentication()
    .AddCallerAuthenticator<AniIdentityLookupAuthenticator>()
    // Where the composite (and any DTMF "press 0 for agent" tool) sends escalations.
    .AddTransferEscalationTarget(ShowcaseWorkflowIds.DefaultEscalationNumber)
    // Composite chain: RealtimeVoice → IntentNlu → DtmfOnly. The composite registers as a
    // Tier 0 (RealtimeVoice) factory, shadowing the inner Realtime factory above thanks to
    // last-wins resolution in CallSessionFactory. Per-call IvrWorkflowState (workflow step,
    // collected data, transcript) and CallerAuthenticationState are preserved across each
    // mid-call swap so the caller doesn't have to re-authenticate when the tier degrades.
    .AddCompositeFallbackStrategy(
        topTier: AgentTier.RealtimeVoice,
        AgentTier.RealtimeVoice,
        AgentTier.IntentNlu,
        AgentTier.DtmfOnly);

// Observer that mirrors caller-auth StrategyEvents into the diagnostics registry.
builder.Services.AddSingleton<ICallObserver, CallerAuthStateObserver>();

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
//app.MapTeams();


app.MapCallAutomation();
app.MapOperatorCalls();
app.MapAuthDiagnostics();
// app.MapOperatorDashboardHub();

//app.MapAgentDiscovery("/agents");
app.MapDefaultEndpoints();

app.Run();


