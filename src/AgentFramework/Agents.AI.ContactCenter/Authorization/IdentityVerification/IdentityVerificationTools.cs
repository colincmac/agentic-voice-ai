using System.ComponentModel;
using Agents.AI.ContactCenter.Authentication.UserIdentity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Authorization.IdentityVerification;

/// <summary>
/// AI tools for identity verification workflows
/// </summary>
public sealed class IdentityVerificationTools
{
    private readonly IIdentityVerificationService _verificationService;
    private readonly ILogger<IdentityVerificationTools> _logger;
    private readonly HashSet<string> _handoffFunctionNames = [];

    public IdentityVerificationTools(
        IIdentityVerificationService verificationService,
        ILogger<IdentityVerificationTools>? logger = null)
    {
        _verificationService = verificationService;
        _logger = logger ?? NullLogger<IdentityVerificationTools>.Instance;
    }

    [Description("Initiates Entra ID identity verification for the participant")]
    public async Task<object> InitiateIdentityVerificationAsync(
        [Description("The participant ID")] string participantId,
        [Description("Required claims for verification (comma-separated)")] string? requiredClaims = null,
        CancellationToken cancellationToken = default)
    {
        var request = new VerificationRequest
        {
            Type = VerificationType.EntraVerifiedID,
            RequiredClaims = string.IsNullOrEmpty(requiredClaims)
                ? new List<string> { "email", "name" }
                : requiredClaims.Split(',').Select(c => c.Trim()).ToList(),
            ExpirationMinutes = 10
        };

        var session = await _verificationService.InitiateVerificationAsync(
            participantId,
            request,
            cancellationToken);

        _logger.LogInformation(
            "Initiated identity verification for participant {ParticipantId}",
            participantId);

        return new
        {
            success = true,
            sessionId = session.SessionId,
            status = session.Status.ToString(),
            expiresAt = session.ExpiresAt,
            message = "Identity verification initiated. Please complete the verification process."
        };
    }

    [Description("Checks the status of an identity verification session")]
    public async Task<object> CheckVerificationStatusAsync(
        [Description("The verification session ID")] string verificationSessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await _verificationService.GetSessionAsync(
            verificationSessionId,
            cancellationToken);

        if (session is null)
        {
            return new { success = false, error = "Verification session not found" };
        }

        return new
        {
            success = true,
            sessionId = session.SessionId,
            status = session.Status.ToString(),
            isVerified = session.Status == VerificationStatus.Verified,
            verifiedAt = session.VerifiedAt,
            verifiedIdentity = session.VerifiedIdentity
        };
    }

    [Description("Completes identity verification with a credential")]
    public async Task<object> CompleteIdentityVerificationAsync(
        [Description("The verification session ID")] string verificationSessionId,
        [Description("The verification credential or code")] string credential,
        CancellationToken cancellationToken = default)
    {
        var result = await _verificationService.VerifyCredentialAsync(
            verificationSessionId,
            credential,
            cancellationToken);

        _logger.LogInformation(
            "Identity verification completed for session {SessionId} with status {Status}",
            verificationSessionId, result.Status);

        return new
        {
            success = result.Success,
            status = result.Status.ToString(),
            isVerified = result.Status == VerificationStatus.Verified,
            verifiedIdentity = result.VerifiedIdentity,
            error = result.ErrorMessage
        };
    }
}
