using Agents.AI.ContactCenter.Agents.AuthorizationAgent;
using Agents.AI.ContactCenter.AITools;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Azure;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.DependencyInjection;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Authorization;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;
using Agents.AI.ContactCenter.Media.Audio;
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
using Showcase.Agent.VoiceAgent;
using Showcase.Agent.VoiceAgent.Apis;
using Showcase.Agent.VoiceAgent.Authentication;
using Showcase.Agent.VoiceAgent.Configuration;
using Showcase.Agent.VoiceAgent.Tools;
using Showcase.Agent.VoiceAgent.Workflow;
using Showcase.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

var azureSection = builder.Configuration.GetSection("Azure");
var tenantId = azureSection["TenantId"];

var credential = new AzureCliCredential();
builder.Services.AddAzureClients(clientBuilder =>
{
    clientBuilder.UseCredential(credential);
});
builder.AddServiceDefaults();

builder.Services.AddHttpClient();

var appConfigEndpoint = builder.Configuration.GetConnectionString("appconfig");
if (!string.IsNullOrWhiteSpace(appConfigEndpoint))
{
    builder.Configuration.AddAzureAppConfiguration(appConfigEndpoint);
}

// ============================================================================
//  AI model clients
// ============================================================================

builder.AddKeyedChatClient("slm")
    .UseOpenTelemetry();

builder.AddKeyedChatClient("chat")
    .UseFunctionInvocation()
    .UseOpenTelemetry();

builder.AddKeyedConversationClient("realtime")
    .UseFunctionInvocation()
    .UseOpenTelemetry();

builder.AddKeyedConversationClient("voicelive")
    .UseFunctionInvocation()
    .UseOpenTelemetry();

builder.Services.AddAzureSpeech(builder.Configuration.GetSection(AzureSpeechServiceOptions.SectionName), options =>
{
    options.Credential = new AzureCliCredential();
});

// ============================================================================
//  Showcase demo infrastructure (caller directory, OTP, etc.)
// ============================================================================

builder.Services.AddSingleton<InMemoryCallerDirectory>();
builder.Services.AddSingleton<ICallerDirectory>(sp => sp.GetRequiredService<InMemoryCallerDirectory>());
builder.Services.AddSingleton<CallerAuthStateRegistry>();

// Per-call scoped tools that reach scoped state (CallerAuthenticationState,
// ICallSessionAccessor). Registered as scoped POCOs; each tool surface is bound to the
// keyed IIvrToolRegistry below.
builder.Services.AddScoped<WorkflowStateTools>();
builder.Services.AddScoped<BalanceLookupTools>();

// Mock SMS-OTP MFA infrastructure.
builder.Services.AddSingleton<LastIssuedOtpRegistry>();
builder.Services.AddSingleton<ISmsOtpSender, LoggingSmsOtpSender>();
builder.Services.AddScoped<SmsOtpAttempt>();

// ============================================================================
//  Tool registrations — IIvrToolRegistry keyed by AgentConfig.TriageAgent.
//  Each tool referenced from YAML by name must resolve through the registry.
// ============================================================================

builder.Services.AddIvrTool(
    AgentConfig.TriageAgent,
    "pin-validator",
    sp => (AIFunction)PinValidationTools.ValidatePinTool(
        sp.GetRequiredService<InMemoryCallerDirectory>(),
        sp.GetRequiredService<ILoggerFactory>()),
    ServiceLifetime.Singleton);

builder.Services.AddIvrTool(
    AgentConfig.TriageAgent,
    "confirm-identity",
    sp => (AIFunction)PinValidationTools.ConfirmIdentityTool(
        sp.GetRequiredService<InMemoryCallerDirectory>(),
        sp.GetRequiredService<ILoggerFactory>()),
    ServiceLifetime.Singleton);

builder.Services.AddIvrTool(
    AgentConfig.TriageAgent,
    "request-otp",
    sp => (AIFunction)SmsOtpTools.RequestOtpTool(sp.GetRequiredService<ILoggerFactory>()),
    ServiceLifetime.Singleton);

builder.Services.AddIvrTool(
    AgentConfig.TriageAgent,
    "submit-otp",
    sp => (AIFunction)SmsOtpTools.SubmitOtpTool(sp.GetRequiredService<ILoggerFactory>()),
    ServiceLifetime.Singleton);

builder.Services.AddIvrTool(
    AgentConfig.TriageAgent,
    "transfer_to_agent",
    _ => (AIFunction)TransferTools.BuildTransferToAgentTool(ShowcaseWorkflowIds.DefaultEscalationNumber),
    ServiceLifetime.Singleton);

builder.Services.AddIvrTool(
    AgentConfig.TriageAgent,
    WorkflowStateTools.RecordCallerNameToolName,
    sp => WorkflowStateTools.BuildRecordCallerNameTool(sp.GetRequiredService<WorkflowStateTools>()),
    ServiceLifetime.Scoped);

builder.Services.AddIvrTool(
    AgentConfig.TriageAgent,
    BalanceLookupTools.LookupBalanceToolName,
    sp => BalanceLookupTools.BuildLookupBalanceTool(sp.GetRequiredService<BalanceLookupTools>()),
    ServiceLifetime.Scoped);

// ============================================================================
//  YAML call workflows loaded into ICallWorkflowCatalog. Passing the agent key
//  wires the compiler to the IIvrToolRegistry above so YAML tool references are
//  validated at host startup.
// ============================================================================

builder.Services.AddCallWorkflowsFromDirectory(
    Path.Combine(AppContext.BaseDirectory, ShowcaseWorkflowIds.SamplesDirectory),
    AgentConfig.TriageAgent);


// ============================================================================
//  Realtime AI agent
// ============================================================================

builder.AddRealtimeAIAgent(
    name: AgentConfig.TriageAgent,
    configurationSection: builder.Configuration.GetSection($"{AgentConfig.SectionName}:{AgentConfig.TriageAgent}"),
    realtimeClientKey: ConfigurationConstants.VoiceLiveConnectionString,
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

// ============================================================================
//  Call session container — new strategies bound to the workflow id.
// ============================================================================
//  Wiring order: caller auth first (so the filter can resolve scoped state),
//  then AddRealtimeAgentBackend to register IRealtimeVoiceBackend + agent wrapping,
//  then the per-workflow strategy factories. The CallerVerificationFilter is wired
//  into the realtime agent's function-invocation middleware (defense-in-depth
//  against the model invoking a guarded tool).

builder.AddCallSessionContainer()
    .AddDistributedCallState(DistributedCallStateBackend.InMemory)
    .AddCallerAuthentication()
    .AddCallerAuthenticator<AniIdentityLookupAuthenticator>()
    .AddPinAuthenticator<InMemoryPinValidator>()
    .AddCallerAuthenticator<SmsOtpAuthenticator>()
    .AddTransferEscalationTarget(ShowcaseWorkflowIds.DefaultEscalationNumber)
    .AddCallControlTools(AgentConfig.TriageAgent)
    // Per-tier strategy factories. The default workflow id is used when a call doesn't
    // specify CallSessionRequest.WorkflowId; with a single registered workflow it's optional.
    .AddRealtimeCallWorkflowStrategy(
        realtimeAgentServiceKey: AgentConfig.TriageAgent)
    .AddNluCallWorkflowStrategy()
    .AddDtmfCallWorkflowStrategy()
    .AddCompositeFallbackStrategy(
        topTier: AgentTier.RealtimeVoice,
        AgentTier.RealtimeVoice,
        AgentTier.IntentNlu,
        AgentTier.DtmfOnly);

// Observer that mirrors caller-auth StrategyEvents into the diagnostics registry.
builder.Services.AddSingleton<ICallObserver, CallerAuthStateObserver>();

// Startup-time prewarm of factories + catalog. Forces every YAML workflow through the
// compiler so missing tool names or broken transitions fail the host on boot rather
// than on the first call. Non-deterministic prewarm errors are still best-effort logged.
builder.Services.AddHostedService<WorkflowPrewarmHostedService>();

// ============================================================================
//  Teams
// ============================================================================

builder.AddAgentApplicationOptions();
builder.Services.AddSingleton<IStorage, MemoryStorage>();

var app = builder.Build();

app.UseRouting();
app.UseWebSockets();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/", async ([FromServices] AuthorizingRealtimeAIAgent agent, CancellationToken cancellationToken) =>
{
    var session = await agent.CreateSessionAsync(null, cancellationToken);
    return "Testing";
});

app.MapCallAutomation();
app.MapOperatorCalls();
app.MapAuthDiagnostics();
app.MapDefaultEndpoints();

app.Run();


