using Agents.AI.Extensions.ToolApproval;
using Microsoft.Extensions.AI;

using Agents.AI.ContactCenter.Authentication.UserIdentity;

namespace Agents.AI.ContactCenter.Authorization.IdentityVerification;

/// <summary>
/// Requires participant identity to be verified before executing a tool
/// </summary>
public sealed class RequiresVerifiedIdentityRequirement : IToolApprovalRequirement
{
    public RequiresVerifiedIdentityRequirement(VerificationLevel level = VerificationLevel.EntraVerifiedID)
    {
        Level = level;
    }

    public VerificationLevel Level { get; }

    public AIContent? OnFailureResponse => new TextContent(
        $"This action requires {Level} identity verification. I'll initiate the verification process.");
}
