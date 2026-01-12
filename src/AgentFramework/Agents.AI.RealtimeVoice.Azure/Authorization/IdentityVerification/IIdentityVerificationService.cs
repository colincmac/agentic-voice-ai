namespace Agents.AI.RealtimeVoice.Azure.Authorization.IdentityVerification;

/// <summary>
/// Service for verifying participant identity using Microsoft Entra Verified ID.
/// Integrates with Entra ID verification flows for secure identity validation.
/// </summary>
public interface IIdentityVerificationService
{
    Task<VerificationSession> InitiateVerificationAsync(
        string participantId,
        VerificationRequest request,
        CancellationToken cancellationToken = default);

    Task<VerificationResult> VerifyCredentialAsync(
        string sessionId,
        string credential,
        CancellationToken cancellationToken = default);

    Task<VerificationSession?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<bool> CancelVerificationAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}
