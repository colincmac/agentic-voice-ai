using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.RealtimeVoice.Azure.Authorization.Biometrics;
using Agents.AI.RealtimeVoice.Azure.Calling;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Agents.AI.Extensions.LiveVoice.IvrWorkflow.IvrOrchestratorConstants;

namespace Showcase.Agent.VoiceAgent.Workflow;

/// <summary>
/// Session activator that uses the IVR orchestrator to coordinate
/// between realtime voice transcripts and the deterministic IVR workflow.
/// </summary>
/// <remarks>
/// This activator is pending refactoring to use the new <see cref="RealtimeIvrWorkflowCoordinator"/>
/// and <see cref="WorkflowIntegratedSessionActivator"/> pattern.
/// </remarks>
[Obsolete("Use WorkflowIntegratedSessionActivator with RealtimeIvrWorkflowCoordinator instead.")]
public sealed class OrchestratorSessionActivator : IContactCenterConversationSessionActivator
{
    private readonly IVoiceBiometricEvaluator _biometricEvaluator;
    private readonly ILoggerFactory _loggerFactory;

    public OrchestratorSessionActivator(
        IVoiceBiometricEvaluator biometricEvaluator,
        ILoggerFactory loggerFactory)
    {
        _biometricEvaluator = biometricEvaluator;
        _loggerFactory = loggerFactory;
    }

    public ContactCenterConversationSession Create(
        string sessionId,
        IServiceScope sessionScope,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger<OrchestratorSessionActivator>();

        // Create the session
        // Note: This activator needs to be refactored to use the new
        // RealtimeIvrWorkflowCoordinator pattern.
        var hubSessionContext = new HubSessionContext(sessionId, sessionScope);
        var session = new ContactCenterConversationSession(sessionScope, hubSessionContext, loggerFactory);

        return session;
    }

    private static async Task BroadcastMessageAsync(
        ContactCenterConversationSession session,
        string text,
        CancellationToken ct)
    {
        var update = new MessageUpdate
        {
            Role = ChatRole.Assistant.ToString(),
            SenderParticipantId = OrchestratorParticipantId,
            Contents = [new TextContent(text)]
        };

        foreach (var participant in session.ParticipantContexts.Values)
        {
            if (participant.ParticipantId != OrchestratorParticipantId)
            {
                await participant.SendMessageAsync(update, ct);
            }
        }
    }

    private static async Task BroadcastAiContentAsync(
        ContactCenterConversationSession session,
        IvrWorkflowState state,
        CancellationToken ct)
    {
        var contents = new List<AIContent>();

        if (state.TryGet("ai_content_bio_start", out BiometricVoiceEnrollmentStarted? startMarker))
        {
            contents.Add(startMarker!);
        }

        if (state.TryGet("ai_content_bio_end", out BiometricVoiceVerificationEnded? endMarker))
        {
            contents.Add(endMarker!);
        }

        if (contents.Count > 0)
        {
            var update = new MessageUpdate
            {
                Role = SystemRole,
                SenderParticipantId = SystemEventsParticipantId,
                Contents = contents
            };

            foreach (var participant in session.ParticipantContexts.Values)
            {
                if (participant.ParticipantId != OrchestratorParticipantId)
                {
                    await participant.SendMessageAsync(update, ct);
                }
            }
        }
    }
}
