using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Showcase.Agent.VoiceAgent.Authentication;

namespace Showcase.Agent.VoiceAgent.Workflow;

/// <summary>
/// E2E showcase workflows that demonstrate caller authentication on top of the new
/// Calling/Proposed strategy stack.
///
/// Two flavours are exposed:
/// <list type="bullet">
///   <item>
///     <see cref="BuildAuthenticatedDtmfWorkflow"/> — DTMF menu that gates "secure" branches
///     (account balance, billing) on a successful PIN entry. The PIN is validated by the
///     <see cref="PinValidationTools.ValidatePinTool(InMemoryCallerDirectory, ILoggerFactory?)"/>
///     bound as the digit-collection validator, which elevates the caller's identity in the
///     scoped <see cref="Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Authentication.CallerAuthenticationState"/>.
///   </item>
///   <item>
///     <see cref="BuildAuthenticatedRealtimeWorkflow"/> — voice-driven companion: the realtime
///     model greets the caller (using the resolved name from ANI), asks for their PIN, and
///     calls <see cref="PinValidationTools.ConfirmIdentityTool"/> to elevate the verification
///     level before answering account-specific questions.
///   </item>
/// </list>
/// Both flows share the same <see cref="ICallerAuthenticator"/> chain registered in DI
/// (ANI lookup → PIN), so the verification state survives across strategy hand-offs.
/// </summary>
public static class AuthenticatedSampleWorkflows
{
    public static RealtimeIvrWorkflowDefinition BuildAuthenticatedDtmfWorkflow(
        InMemoryCallerDirectory directory,
        ILoggerFactory loggerFactory)
    {
        var validator = PinValidationTools.ValidatePinTool(directory, loggerFactory);

        return RealtimeIvrWorkflowBuilder.Create("authenticated-dtmf")
            .WithBasePrompt(prompt => prompt
                .WithRole("ACME Bank IVR", "Route callers to self-service or live agents.")
                .WithPersonality(p => p.Personality("calm, clear").Tone("warm")))
            .AddStep(step => step
                .WithId("welcome")
                .WithGoal("Greet the caller and present main menu")
                .WithDescription("Welcome to ACME Bank.")
                .AddInstruction("Greet the caller, then route by digit.")
                .ExitWhen("caller selects a menu option")
                .WithDtmfMenu(menu => menu
                    .WithPromptOverride("""
                        <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="en-US">
                          <voice name="en-US-Ava:DragonHDLatestNeural">
                            <prosody rate="-5%">
                              Welcome to ACME Bank.
                              <break time="300ms"/>
                              For account balance, press 1.
                              <break time="200ms"/>
                              For billing, press 2.
                              <break time="200ms"/>
                              To speak with an agent, press 0.
                            </prosody>
                          </voice>
                        </speak>
                        """)
                    .Option('1', "balance", "auth_pin")
                    .Option('2', "billing", "auth_pin")
                    .Option('0', "agent", "transfer_to_agent")))
            .AddStep(step => step
                .WithId("auth_pin")
                .WithGoal("Collect and validate the caller's PIN")
                .WithDescription("PIN entry")
                .AddInstruction("Prompt the caller for their four-digit PIN, then route based on the validator's result.")
                .WithTool(validator)
                .ExitWhen("PIN validated or attempts exhausted")
                .TransitionTo("balance_self_service", "PIN valid")
                .TransitionTo("transfer_to_agent", "PIN invalid")
                .WithDtmfMenu(menu => menu
                    .WithPromptOverride("""
                        <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="en-US">
                          <voice name="en-US-Ava:DragonHDLatestNeural">
                            Please enter your four-digit PIN, followed by the pound key.
                          </voice>
                        </speak>
                        """)
                    .WithMinNumberOfDigits(4)
                    .WithMaxNumberOfDigits(4)
                    .WithTerminationDigit('#')
                    .ValidateWith(
                        validator,
                        digitsParameterName: "digits",
                        onValidNextStepId: "balance_self_service",
                        onInvalidPrompt: "That PIN doesn't match our records. Transferring you to an agent.")))
            .AddStep(step => step
                .WithId("balance_self_service")
                .WithGoal("Read the verified caller their account balance")
                .WithDescription("Balance self-service")
                .AddInstruction("Caller is fully verified at this point. Read account balance and offer next actions.")
                .RequiresAuth(AuthenticationLevel.FullyAuthenticated)
                .ExitWhen("Caller is finished or asks for an agent"))
            .AddStep(step => step
                .WithId("transfer_to_agent")
                .WithGoal("Hand the caller to a live agent")
                .WithDescription("Agent transfer")
                .AddInstruction("Tell the caller you're transferring them, then disconnect.")
                .ExitWhen("Transfer initiated"))
            .Build();
    }

    public static RealtimeIvrWorkflowDefinition BuildAuthenticatedRealtimeWorkflow(
        InMemoryCallerDirectory directory,
        ILoggerFactory loggerFactory)
    {
        var confirmIdentity = PinValidationTools.ConfirmIdentityTool(directory, loggerFactory);

        return RealtimeIvrWorkflowBuilder.Create("authenticated-realtime")
            .WithBasePrompt(prompt => prompt
                .WithRole("ACME Bank concierge agent",
                    "Help callers with account inquiries after verifying identity.")
                .WithPersonality(p => p
                    .Personality("warm, concise, professional")
                    .Tone("friendly and confident"))
                .WithContext("If the conversation context has a CallerName, address them by first name."))
            .WithGreeting(
                "Welcome to ACME Bank. I can help with balances, billing, or transferring to a representative.",
                step => step
                    .AddInstruction("If a CallerName is present in context, greet by first name. Otherwise greet generically.")
                    .AddInstruction("Listen for the caller's intent: balance, billing, or agent transfer.")
                    .ExitWhen("Caller has stated their primary intent")
                    .TransitionTo("verify", "Caller wants account-specific information")
                    .TransitionTo("transfer", "Caller asked for an agent"))
            .AddStep(step => step
                .WithId("verify")
                .WithGoal("Confirm the caller's identity via PIN before sharing account info")
                .WithDescription("Identity verification")
                .AddInstruction("Politely ask the caller to say their four-digit PIN.")
                .AddInstruction("When they say it, call the confirm_identity tool with the digits.")
                .AddInstruction("On failure, ask once more, then offer to transfer to an agent.")
                .WithTool(confirmIdentity)
                .ExitWhen("Identity confirmed or transfer requested")
                .TransitionTo("self_service", "Identity confirmed")
                .TransitionTo("transfer", "Identity could not be confirmed"))
            .AddStep(step => step
                .WithId("self_service")
                .WithGoal("Answer the caller's account-specific question")
                .WithDescription("Verified self-service")
                .AddInstruction("Caller is verified — answer their original request using account tools.")
                .AddInstruction("When they're done, ask if there's anything else.")
                .RequiresAuth(AuthenticationLevel.FullyAuthenticated)
                .ExitWhen("Caller has no more questions"))
            .AddStep(step => step
                .WithId("transfer")
                .WithGoal("Transfer the caller to a live agent")
                .WithDescription("Agent transfer")
                .AddInstruction("Acknowledge the request, set expectations on wait time, and complete the transfer.")
                .ExitWhen("Transfer initiated"))
            .WithClosing(
                "Thanks for calling ACME Bank. Goodbye.",
                step => step.AddInstruction("Summarize what was accomplished and end the call."))
            .Build();
    }
}
