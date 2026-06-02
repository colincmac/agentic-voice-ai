using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Azure;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.DependencyInjection;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.DependencyInjection;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.Extensions.AITools;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.Hosting;
using Agents.AI.Realtime;
using Azure.AI.VoiceLive;
using Azure.Identity;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Azure;
using OpenTelemetry.Resources;
using Pipelines.Sockets.Unofficial.Arenas;
using Showcase.Agent.VoiceAgent;
using Showcase.Agent.VoiceAgent.Apis;
using Showcase.Agent.VoiceAgent.Authentication;
using Showcase.Agent.VoiceAgent.Configuration;
using Showcase.Agent.VoiceAgent.Tools;
using Showcase.Agent.VoiceAgent.Workflow;
using Showcase.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
//builder.Services.AddGrpc();
var azureSection = builder.Configuration.GetSection("Azure");
var tenantId = azureSection["TenantId"];

var credential = new AzureCliCredential();
builder.Services.AddAzureClients(clientBuilder =>
{
    // Make this the default for clients created by the factory
    clientBuilder.UseCredential(credential);
});
builder.AddServiceDefaults();


builder.Services.AddHttpClient();

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

// AI Model Clients
builder.AddKeyedChatClient("slm")
    .UseOpenTelemetry(sourceName: "Showcase.VoiceAgent");

builder.AddKeyedChatClient("chat")
    .UseFunctionInvocation()
    .UseOpenTelemetry(sourceName: "Showcase.VoiceAgent");

builder.AddKeyedConversationClient("realtime")
    .UseFunctionInvocation()
    .UseOpenTelemetry(sourceName: "Showcase.VoiceAgent");

builder.AddKeyedConversationClient("voicelive")
    .UseFunctionInvocation()
    .UseOpenTelemetry(sourceName: "Showcase.VoiceAgent");

builder.Services.AddAzureSpeech(builder.Configuration.GetSection(AzureSpeechServiceOptions.SectionName), options =>
{
    options.Credential = new AzureCliCredential();
});

// E2E showcase: register the auth-aware DTMF workflow as the default and the realtime
// equivalent under a tier-keyed slot, so the CallingApi can pick either via ?tier=.
builder.Services.AddSingleton<InMemoryCallerDirectory>();
builder.Services.AddSingleton<ICallerDirectory>(sp => sp.GetRequiredService<InMemoryCallerDirectory>());
builder.Services.AddSingleton<CallerAuthStateRegistry>();
builder.Services.AddScoped<IAIToolCollection, WorkflowStateTools>();
builder.Services.AddScoped<IAIToolCollection, BalanceLookupTools>();

// Mock SMS-OTP MFA: in-process sender that logs the generated code and stashes it in
// LastIssuedOtpRegistry so the diagnostics API can surface it during demo runs.
builder.Services.AddSingleton<LastIssuedOtpRegistry>();
builder.Services.AddSingleton<ISmsOtpSender, LoggingSmsOtpSender>();
// SmsOtpAttempt is consumed by SmsOtpAuthenticator + SmsOtpTools within the same call scope.
builder.Services.AddScoped<SmsOtpAttempt>();

// Declarative YAML IVR framework: loads workflow definitions from
// Workflow\Samples\*.yaml (copied to the app output via the csproj content glob),
// compiles them into RealtimeIvrWorkflowDefinition instances, and exposes the
// caller-auth tools (`pin-validator`, `confirm-identity`, `transfer-to-agent`) under
// names the YAML samples reference. Showcase predicates and additional sources can
// be appended here.
builder.Services.AddIvrWorkflowFramework(b => b
    .AddFileSystemSource(Path.Combine(AppContext.BaseDirectory, DemoWorkflowIds.SamplesDirectory))
    .AddTool("pin-validator", sp => PinValidationTools.ValidatePinTool(
        sp.GetRequiredService<InMemoryCallerDirectory>(),
        sp.GetRequiredService<ILoggerFactory>()))
    .AddTool("confirm-identity", sp => PinValidationTools.ConfirmIdentityTool(
        sp.GetRequiredService<InMemoryCallerDirectory>(),
        sp.GetRequiredService<ILoggerFactory>()))
    .AddTool("request-otp", sp => SmsOtpTools.RequestOtpTool(
        sp.GetRequiredService<ILoggerFactory>()))
    .AddTool("submit-otp", sp => SmsOtpTools.SubmitOtpTool(
        sp.GetRequiredService<ILoggerFactory>()))
    // .AddTool("lookup-balance", sp => BalanceLookupTools.LookupBalanceTool(
    //     sp.GetRequiredService<InMemoryCallerDirectory>(),
    //     sp.GetRequiredService<ILoggerFactory>()))
    // .AddTool("record-caller-name", sp => WorkflowStateTools.RecordCallerNameTool(
    //     sp.GetRequiredService<ILoggerFactory>()))
    .AddTool("transfer-to-agent", _ => TransferTools.BuildTransferToAgentTool(
        DemoWorkflowIds.DefaultEscalationNumber)));


builder.AddRealtimeAIAgent(
    name: AgentConfig.TriageAgent,
    configurationSection: builder.Configuration.GetSection($"{AgentConfig.SectionName}:{AgentConfig.TriageAgent}"),
    liveConversationClientKey: "voicelive",
    configureOptions: agentOptions =>
    {
        agentOptions.SessionOptions = agentOptions.SessionOptions.With(
            rawRepresentationFactory: () => new VoiceLiveSessionOptions
            {
                TurnDetection = new AzureSemanticVadTurnDetection
                {
                    Threshold = 0.3f,
                    PrefixPadding = TimeSpan.FromMilliseconds(100),
                    SilenceDuration = TimeSpan.FromMilliseconds(200),
                    RemoveFillerWords = true,
                },
                Voice = new AzureStandardVoice("en-US-Ava:DragonHDLatestNeural") { Temperature = 0.3f },
                InputAudioNoiseReduction = new AudioNoiseReduction(AudioNoiseReductionType.NearField),
                InputAudioEchoCancellation = new AudioEchoCancellation(),
                Temperature = 0.8f,
                ToolChoice = ToolChoiceLiteral.Auto
            });
    });


builder.Services.AddSingleton<RealtimeIvrWorkflowDefinition>(sp =>
    DemoWorkflowLoader.Load(sp, DemoWorkflowIds.AuthenticatedRealtime));

builder.Services.AddKeyedSingleton<RealtimeIvrWorkflowDefinition>(
    nameof(AgentTier.DtmfOnly),
    (sp, _) => DemoWorkflowLoader.Load(sp, DemoWorkflowIds.AuthenticatedDtmf));

builder.Services.AddKeyedSingleton<RealtimeIvrWorkflowDefinition>(
    nameof(AgentTier.RealtimeVoice),
    (sp, _) => DemoWorkflowLoader.Load(sp, DemoWorkflowIds.AuthenticatedRealtime));

builder.Services.AddKeyedSingleton<RealtimeIvrWorkflowDefinition>(
    nameof(AgentTier.IntentNlu),
    (sp, _) => DemoWorkflowLoader.Load(sp, DemoWorkflowIds.NluWithDtmfFallback));

builder.AddCallSessionContainer()
    .AddDistributedCallState(DistributedCallStateBackend.InMemory)
    // Inner factories — the composite below shadows the top tier and reuses these
    // through DI. Order matters: register the inner tiers BEFORE the composite so
    // the composite's lookup finds them.
    .AddRealtimeVoiceStrategy(realtimeAgentServiceKey: AgentConfig.TriageAgent)
    .AddNluStrategy(chatClientServiceKey: "slm")
    .AddDtmfStreamingStrategy()
    .AddCallControlTools()
    // Caller authentication: ANI lookup against the in-memory directory plus the
    // anonymous fallback so unknown callers still walk the workflow as guests. PIN
    // elevation routes through the orchestrator via PinAuthenticator + IPinValidator
    // so PIN-collecting tools mutate state through the same pipeline as ANI.
    .AddCallerAuthentication()
    .AddCallerAuthenticator<AniIdentityLookupAuthenticator>()
    .AddPinAuthenticator<InMemoryPinValidator>()
    // Demo MFA second factor: SMS OTP. Uses LoggingSmsOtpSender + the framework's
    // InMemoryChallengeStore registered by AddCallerAuthentication() above. The SmsOtpAttempt
    // scoped buffer the SmsOtpTools fill is added here so it shares the call's DI scope.
    .AddCallerAuthenticator<SmsOtpAuthenticator>()
    // Where the composite (and any DTMF "press 0 for agent" tool) sends escalations.
    .AddTransferEscalationTarget(DemoWorkflowIds.DefaultEscalationNumber)
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

// Startup-time warm-up of the per-tier strategy factories and keyed workflow definitions.
builder.Services.AddHostedService<WorkflowPrewarmHostedService>();

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

//app.UseHttpsRedirection();

//app.UseAuthentication();
//app.UseAuthorization();
//app.MapAgentIdentityManagement();

app.MapGet("/", async ([FromServices] AuthorizingRealtimeAIAgent agent, CancellationToken cancellationToken) =>
{
    var session = await agent.CreateRealtimeSessionAsync(null, cancellationToken);
    return "Testing";
});
//app.MapTeams();


app.MapCallAutomation();
app.MapOperatorCalls();
app.MapAuthDiagnostics();
// app.MapOperatorDashboardHub();

//app.MapAgentDiscovery("/agents");
app.MapDefaultEndpoints();

app.Run();


