using Agents.AI.Extensions.ToolApproval;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Authorization.IdentityVerification;

/// <summary>
/// Handles identity verification requirements
/// </summary>
public sealed class IdentityVerificationHandler : ToolApprovalHandler<RequiresVerifiedIdentityRequirement>
{
    private readonly ILogger<IdentityVerificationHandler> _logger;
    private readonly IIdentityVerificationService _verificationService;

    public IdentityVerificationHandler(
        IIdentityVerificationService verificationService,
        ILogger<IdentityVerificationHandler>? logger = null)
    {
        _verificationService = verificationService;
        _logger = logger ?? NullLogger<IdentityVerificationHandler>.Instance;
    }

    protected override async Task HandleRequirementAsync(
        ToolApprovalContext context,
        RequiresVerifiedIdentityRequirement requirement)
    {
        var participantId = GetParticipantId(context);

        // Check if identity is already verified at required level
        if (context.Arguments.TryGetValue("verifiedIdentity", out var identity) && identity is not null)
        {
            _logger.LogInformation(
                "Identity already verified for participant {ParticipantId}",
                participantId);

            context.Succeed(requirement);
            return;
        }

        _logger.LogInformation(
            "Identity verification required at level {Level} for participant {ParticipantId}",
            requirement.Level, participantId);

        context.Fail(requirement);
        await Task.CompletedTask;
    }

    private string GetParticipantId(ToolApprovalContext context)
    {
        return context.Arguments.TryGetValue("participantId", out var participantId) && participantId is string pid
            ? pid
            : "default";
    }
}
