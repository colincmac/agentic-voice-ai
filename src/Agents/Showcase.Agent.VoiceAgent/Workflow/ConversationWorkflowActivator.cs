using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Agents.AI.RealtimeVoice.Azure.Authorization.Biometrics;
using Agents.AI.RealtimeVoice.Azure.Calling;

namespace Showcase.Agent.VoiceAgent.Workflow;

/// <summary>
/// Provides workflow definitions for Contact Center IVR flows.
/// Use with <see cref="WorkflowIntegratedSessionActivator"/> via the
/// <c>AddWorkflowIntegration</c> extension method.
/// </summary>
public static class ConversationWorkflowFactory
{
    /// <summary>
    /// Creates the default caller intent and biometric workflow definition.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>The workflow definition for the session.</returns>
    public static RealtimeIvrWorkflowDefinition CreateCallerIntentWorkflow(string sessionId)
    {
        return RealtimeIvrWorkflowBuilder.Create("CallerIntentBiometric")
            .WithBasePrompt(prompt => prompt
                .WithRole("financial services contact center agent", "Help callers with account inquiries and service requests")
                .WithPersonality(p => p
                    .Personality("professional, helpful, and efficient")
                    .Tone("warm and confident"))
                .WithContext("You are assisting callers at ACME Financial Services.")
                .AddPronunciation("ACME", "Ak-mee")
                .AddPronunciation("IVR", "I-V-R"))
            .WithGreeting(
                "Welcome to ACME Financial Services. I'm an AI assistant here to help you today. How can I assist you?",
                step => step
                    .AddInstruction("Listen for the caller's initial intent")
                    .AddInstruction("If unclear, ask a clarifying question"))
            .AddStep(step => step
                .WithId("2_collect_intent")
                .WithGoal("Understand the caller's primary reason for calling")
                .WithDescription("Collect and confirm the caller's intent")
                .AddInstruction("Categorize the intent: account inquiry, transaction, support, or other")
                .AddInstruction("Confirm understanding by restating the intent briefly")
                .AddExample("I understand you're calling about your recent transaction. Let me help you with that.")
                .ExitWhen("Intent is confirmed by caller")
                .TransitionTo("3_verify_identity", "Intent confirmed"))
            .AddStep(step => step
                .WithId("3_verify_identity")
                .WithGoal("Verify the caller's identity before proceeding")
                .WithDescription("Collect identity information for verification")
                .AddInstruction("Ask for the caller's registered name")
                .AddInstruction("Confirm the name before proceeding to voice verification")
                .RequiresAuth(AuthenticationLevel.None)
                .ExitWhen("Caller provides their name")
                .TransitionTo("4_biometric_verification", "Name collected"))
            .AddStep(step => step
                .WithId("4_biometric_verification")
                .WithGoal("Perform voice biometric verification")
                .WithDescription("Guide caller through voice verification process")
                .AddInstruction("Explain that voice verification helps protect their account")
                .AddInstruction("Ask caller to repeat: 'My voice is my password, verify me'")
                .AddInstruction("Wait for biometric tool to complete verification")
                .AddInstruction("If verification fails, inform caller and offer to connect to a representative")
                .RequiresAuth(AuthenticationLevel.AccountVerified)
                // Note: Tools will be resolved from the tool collection at runtime
                .ExitWhen("Voice verification completes successfully or fails")
                .TransitionTo("5_handle_request", "Verification succeeded"))
            .AddStep(step => step
                .WithId("5_handle_request")
                .WithGoal("Process the caller's verified request")
                .WithDescription("Handle the caller's request now that identity is verified")
                .AddInstruction("Access appropriate tools based on the confirmed intent")
                .AddInstruction("Provide clear information about the request status")
                .RequiresAuth(AuthenticationLevel.FullyAuthenticated)
                .ExitWhen("Request is handled or needs escalation"))
            .WithClosing(
                "Is there anything else I can help you with today? Thank you for calling ACME Financial Services.",
                step => step
                    .AddInstruction("Summarize actions taken")
                    .AddInstruction("Provide any reference numbers or next steps"))
            .Build();
    }

    /// <summary>
    /// Creates a simple workflow for testing purposes.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>A minimal workflow definition.</returns>
    public static RealtimeIvrWorkflowDefinition CreateSimpleWorkflow(string sessionId)
    {
        return RealtimeIvrWorkflowBuilder.Create("SimpleAssistant")
            .WithBasePrompt(prompt => prompt
                .WithRole("helpful AI assistant", "Help users with their requests")
                .WithPersonality(p => p
                    .Personality("friendly and efficient")))
            .WithGreeting("Hello! How can I help you today?")
            .AddStep(step => step
                .WithId("2_assist")
                .WithGoal("Help the caller with their request")
                .WithDescription("General assistance step")
                .AddInstruction("Listen to the caller's request")
                .AddInstruction("Provide helpful information or guidance")
                .ExitWhen("Caller's request is addressed"))
            .WithClosing("Is there anything else I can help you with? Have a great day!")
            .Build();
    }
}

