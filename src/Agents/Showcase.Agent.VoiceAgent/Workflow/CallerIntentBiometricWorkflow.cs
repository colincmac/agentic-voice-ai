using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.RealtimeVoice.Azure.Authorization.Biometrics;

namespace Showcase.Agent.VoiceAgent.Workflow;

/// <summary>
/// Example IVR workflow for caller intent detection and biometric verification.
/// </summary>
/// <remarks>
/// This workflow is pending refactoring to use the new <see cref="RealtimeIvrWorkflowBuilder"/> API.
/// The old IvrWorkflowBuilder API has been updated and this workflow needs to be migrated.
/// </remarks>
[Obsolete("Pending migration to new RealtimeIvrWorkflowBuilder API.")]
public static class CallerIntentBiometricWorkflow
{
    /// <summary>
    /// Creates a caller intent and biometric verification workflow.
    /// </summary>
    /// <remarks>
    /// TODO: Migrate to use RealtimeIvrWorkflowBuilder with proper step configuration.
    /// </remarks>
    public static IvrWorkflowDefinition Create(string participantId, IVoiceBiometricEvaluator biometricEvaluator)
    {
        // This is a placeholder that returns a minimal workflow.
        // The full implementation needs to be migrated to the new API.
        return IvrWorkflowBuilder.Create("CallerIntentBiometric")
            .WithWelcomeMessage("Welcome to the contact center. I'm an AI agent here to assist you.")
            .WithCompletionMessage("Thank you. I have verified your identity and intent.")
            .WithFailureMessage("I'm sorry, I was unable to complete the verification process.")
            .AddInputStep(
                name: "CollectIntent",
                orchestratorInstructions: "Determine the caller's intent from their response.",
                voiceAgentInstructions: "In a few words, please tell me the reason for your call today.",
                stateKey: "callerIntent",
                configure: step => step.WithMaxRetries(2))
            .AddInputStep(
                name: "CollectName",
                orchestratorInstructions: "Collect the caller's full name for identity verification.",
                voiceAgentInstructions: "Please say your first and last name.",
                stateKey: "callerName",
                configure: step => step.RequiresPreviousStep("CollectIntent").WithMaxRetries(3))
            .Build();
    }
}
