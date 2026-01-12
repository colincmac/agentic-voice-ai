using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.Extensions.ToolApproval;
using Agents.AI.RealtimeVoice.Azure.Authorization.Biometrics;
using Agents.AI.RealtimeVoice.Azure.Authorization.FraudCheck;
using Agents.AI.RealtimeVoice.Azure.Authorization.IdentityVerification;
using Agents.AI.RealtimeVoice.Azure.Authorization.VoiceApproval;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Azure.Communication.CallAutomation;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.RealtimeVoice.Azure.Calling;


public sealed class HubSessionContext
{


    /// <summary>
    /// Creates a HubSessionContext 
    /// </summary>
    public HubSessionContext(string sessionId, IServiceScope sessionScope)
    {
        SessionId = sessionId;
        CallAutomation = sessionScope.ServiceProvider.GetRequiredService<CallAutomationClient>();
        //OrchestratingAgent = sessionScope.ServiceProvider.GetRequiredService<AIAgent>();
        AuthorizingAgent = sessionScope.ServiceProvider.GetRequiredService<AuthorizingRealtimeAIAgent>();
        ApprovalHandlerProvider = sessionScope.ServiceProvider.GetRequiredService<IToolApprovalHandlerProvider>();
        ToolApprovalStore = sessionScope.ServiceProvider.GetRequiredService<IToolApprovalStore>();
        LocalSessionApprovalStore = sessionScope.ServiceProvider.GetRequiredService<VoiceApprovalStore>();

        IdentityVerification = sessionScope.ServiceProvider.GetService<IIdentityVerificationService>();
        FraudDetection = sessionScope.ServiceProvider.GetService<IFraudDetectionMonitor>();
        VoiceBiometrics = sessionScope.ServiceProvider.GetService<IVoiceBiometricEvaluator>();
    }


    //public AIAgent OrchestratingAgent { get; }
    public AuthorizingRealtimeAIAgent AuthorizingAgent { get; }

    public CallAutomationClient CallAutomation { get; }
    /// <summary>
    /// Approval handler provider for tool approval workflow
    /// </summary>
    public IToolApprovalHandlerProvider ApprovalHandlerProvider { get; }

    /// <summary>
    /// Tool approval store for managing tool-specific approval requests
    /// </summary>
    public IToolApprovalStore ToolApprovalStore { get; }

    public VoiceApprovalStore LocalSessionApprovalStore { get; }

    public string SessionId { get; }
    /// <summary>
    /// Entra identity verification service
    /// </summary>
    public IIdentityVerificationService? IdentityVerification { get; }

    /// <summary>
    /// Fraud detection monitor
    /// </summary>
    public IFraudDetectionMonitor? FraudDetection { get; }

    /// <summary>
    /// Voice biometric evaluator
    /// </summary>
    public IVoiceBiometricEvaluator? VoiceBiometrics { get; }


}
