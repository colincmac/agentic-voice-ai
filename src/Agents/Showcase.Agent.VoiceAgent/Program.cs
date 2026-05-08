using A2A.AspNetCore;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
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

// var prompt = RealtimePrompt.CreateBuilder()
//     .WithRole(
//         identity: "You are a helpful credit card dispute specialist for Woodgrove Bank",
//         objective: "Assist customers in filing disputes for unauthorized or incorrect charges on their Woodgrove credit card")
//     .WithPersonality(p => p
//         .Personality("Professional, empathetic, and reassuring")
//         .Tone("Calm, supportive, and patient")
//         .Length("1-2 sentences per turn")
//         .WithLanguage("English",true)
//         .Pacing("Speak clearly and at a moderate pace. Don't change your pace based on the customer's speech, but you can repeat things more slowly if they didn't understand you the first time.")
//         .Emotion("Show understanding when customers express frustration about disputed charges")
//         .EnforceVariety())
//     .AddPronunciation("Woodgrove", "WOOD-grove")
//     .WithInstructions(i => i
//         .AddRules(
//             "ALWAYS verify the customer's identity before discussing account details",
//             "NEVER read the full card number aloud—only the last 4 digits",
//             "IF the customer provides a transaction date or amount, repeat it back to confirm",
//             "IF the dispute amount exceeds $500, inform the customer a specialist may follow up within 48 hours")
//         .HandleUnclearAudio(
//             askForClarification: true,
//             clarificationPhrases: [
//                 "I didn't catch that. Could you repeat the transaction details?",
//                 "Sorry, I missed that. Can you say that again?"
//             ]))
//     .WithTools(t => t
//         .GlobalPreamble("Tool calls might be quick or take a little while. While calling a tool, always inform the customer that you are doing something on their behalf.")
//         .AddPreambleTool(
//             name: "lookup_account",
//             useWhen: "verifying customer identity or retrieving account information",
//             doNotUseWhen: "the customer has not provided any identifying information",
//             preamblePhrases: [
//                 "Thank you, hold on while I look up your account."
//                 ])
//         .AddProactiveTool(
//             name: "get_recent_transactions",
//             useWhen: "the customer wants to identify or review recent charges",
//             doNotUseWhen: "account has not been verified")
//         .AddPreambleTool(
//             name: "submit_dispute",
//             useWhen: "all dispute details have been collected and confirmed",
//             doNotUseWhen: "transaction ID or dispute reason is missing",
//             preamblePhrases: [
//                 "Okay, let me submit this and I'll provide you with a confirmation number. Please wait."
//                 ])
//         .AddPreambleTool(
//             name: "check_dispute_status",
//             useWhen: "customer asks about an existing dispute",
//             preamblePhrases: [
//                 "Let me check the status of your dispute.",
//                 "I'll pull up your dispute details now."
//             ]))
//     .AddConversationState(s => s
//         .Id("1_greeting")
//         .Goal("Welcome the customer and identify the reason for calling")
//         .Description("Greet the caller and establish context")
//         .AddInstructions(
//             "Identify as Woodgrove Bank Dispute Services",
//             "Keep the greeting brief and invite the customer to share their concern")
//         .AddExamples(
//             "Thanks for calling Woodgrove Bank Dispute Services. How can I help you today?",
//             "You've reached Woodgrove Bank. What can I assist you with?")
//         .ExitWhen("Customer states they want to dispute a charge")
//         .TransitionTo("2_verify", "After greeting the customer"))
//     .AddConversationState(s => s
//         .Id("2_verify")
//         .Goal("Verify the customer's identity")
//         .Description("Collect identifying information and verify account ownership")
//         .AddInstructions(
//             "Ask for the last 4 digits of the card and the account holder's date of birth",
//             "Call lookup_account to verify identity",
//             "IF verification fails, offer to retry once or escalate to a human agent")
//         .AddExamples(
//             "To help you, I'll need to verify your identity. Can you provide the last 4 digits of your card?",
//             "And what is the date of birth on the account?")
//         .ExitWhen("Account is verified successfully")
//         .TransitionTo("3_identify_transaction", "After verifying the customer's identity"))
//     .AddConversationState(s => s
//         .Id("3_identify_transaction")
//         .Goal("Identify the transaction to dispute")
//         .Description("Help the customer locate the charge in question")
//         .AddInstructions(
//             "Ask if the customer knows the date or amount of the charge",
//             "Call get_recent_transactions to display recent activity",
//             "Confirm the specific transaction with the customer")
//         .AddExamples(
//             "Do you know the date or amount of the charge you'd like to dispute?",
//             "I see a charge for $42.50 at MerchantName on January 15th. Is that the one?")
//         .ExitWhen("Customer confirms the transaction to dispute")
//         .TransitionTo("4_collect_reason", "After identifying the transaction to dispute"))
//     .AddConversationState(s => s
//         .Id("4_collect_reason")
//         .Goal("Collect the dispute reason")
//         .Description("Determine why the customer is disputing the charge")
//         .AddInstructions(
//             "Ask why the customer is disputing this charge",
//             "Common reasons: unauthorized charge, duplicate charge, incorrect amount, merchandise not received, service not provided",
//             "Summarize the reason back to the customer for confirmation",
//             "If the customer identifies the reason for the dispute in the previous steps, use that reason but confirm whether they want to use more information")
//         .AddExamples(
//             "Can you tell me why you're disputing this charge?",
//             "So you're saying you didn't authorize this transaction—is that correct?")
//         .ExitWhen("Dispute reason is confirmed")
//         .TransitionTo("5_submit", "After collecting the dispute reason"))
//     .AddConversationState(s => s
//         .Id("5_submit")
//         .Goal("Submit the dispute and provide next steps")
//         .Description("File the dispute and inform the customer of the process")
//         .AddInstructions(
//             "Call submit_dispute with all collected details",
//             "Provide the dispute reference number to the customer",
//             "Explain provisional credit will be applied within 3-5 business days",
//             "Inform that investigation may take up to 60 days")
//         .AddExamples(
//             "Your dispute has been submitted. Your reference number is D-1234567.",
//             "You'll see a provisional credit on your account within 3-5 business days while we investigate.")
//         .ExitWhen("Customer confirms understanding of next steps")
//         .TransitionTo("6_closing", "After submitting the dispute"))
//     .AddConversationState(s => s
//         .Id("6_closing")
//         .Goal("Close the call professionally")
//         .Description("Thank the customer and offer additional assistance")
//         .AddInstruction("After the customer acknowledges that there are no further issues, end the conversation.")
//         .AddExamples(
//             "Is there anything else I can help you with today?",
//             "Thank you for calling Woodgrove Bank. Have a great day!")
//         .ExitWhen("Customer ends the call"))
//     .WithSafety(s => s
//         .UseDefaultEscalationConditions()
//         .MaxFailedToolAttempts(2))
//     .BuildAndRender();

// Console.WriteLine(prompt);

// builder.AddRealtimeAIAgent(
//     name: AgentConfig.TriageAgent,
//     configurationSection: builder.Configuration.GetSection($"{AgentConfig.SectionName}:{AgentConfig.TriageAgent}"),
//     liveConversationClientKey: "voicelive", configureOptions: (opt) => {
//         opt.SessionOptions = opt.SessionOptions.With(instructions: prompt);
//     });

// builder.AddAIAgent(
//     name: "IvrOrchestrator",
//     instructions: """
//         You analyze voice conversation transcripts and determine when workflow step transitions should occur.
//         Your decisions help guide the IVR workflow through greeting, intent collection, identity verification,
//         and request handling phases.
//         """,
//     chatClientServiceKey: "chat");

// builder.AddAIAgent(
//     name: "IntentAgent",
//     instructions: """
//         You analyze voice conversation transcripts and determine when workflow step transitions should occur.
//         Your decisions help guide the IVR workflow through greeting, intent collection, identity verification,
//         and request handling phases.
//         """,
//     chatClientServiceKey: "chat");

// builder.AddTestAgents();
// builder.AddConversationHub(
//     opt => builder.Configuration.GetSection(CommunicationOptions.SectionName).Bind(opt),
//     opt =>
//     {
//         opt.RealtimeAgentServiceKey = AgentConfig.TriageAgent;
//     })
//     .AddCallAutomation(false)
//     // .AddToolCollection<WoodgroveDisputeTools>()
//     // .AddOperatorDashboard()
//     //.AddBiometricVoiceEvaluation()
//     .AddStubCallAnalytics();
// // Add workflow integration with the orchestrator agent and workflow factory
// //.AddWorkflowIntegration(
// //    orchestratorAgentFactory: sp => sp.GetRequiredKeyedService<AIAgent>("IvrOrchestrator"),
// //    workflowFactory: ConversationWorkflowFactory.CreateCallerIntentWorkflow);

// New Calling/Proposed shape: registers ICallSessionFactory + ICallSessionRegistry +
// ICallQualityReporter, and wires the realtime voice strategy on top of the existing
// AuthorizingRealtimeAIAgent. ISpeechSynthesizer would be added separately to enable DTMF.
builder.Services.Configure<CommunicationOptions>(builder.Configuration.GetSection(CommunicationOptions.SectionName));
builder.Services.AddSingleton<RealtimeIvrWorkflowDefinition>(sp =>
    ConversationWorkflowFactory.CreateCallerIntentWorkflow(sessionId: "default"));

// The realtime agent that the new realtime backend wraps. Reads its config from
// Agents:TriageAgent and uses the "voicelive" conversation client registered above.
builder.AddRealtimeAIAgent(
    name: AgentConfig.TriageAgent,
    configurationSection: builder.Configuration.GetSection($"{AgentConfig.SectionName}:{AgentConfig.TriageAgent}"),
    liveConversationClientKey: "voicelive");

builder.AddCallSessionContainer()
    .AddAcsCallAutomation()
    .AddRealtimeVoiceStrategy(realtimeAgentServiceKey: AgentConfig.TriageAgent)
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
