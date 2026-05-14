using Agents.AI.Extensions.ToolApproval;

using Agents.AI.ContactCenter.Authentication.UserIdentity;

namespace Agents.AI.ContactCenter.Authorization.IdentityVerification;

// Attribute to mark tools as requiring verified identity
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresVerifiedIdentityAttribute : Attribute, IToolApprovalRequirementData
{
    public VerificationLevel RequiredLevel { get; }

    public RequiresVerifiedIdentityAttribute(VerificationLevel level = VerificationLevel.EntraVerifiedID)
    {
        RequiredLevel = level;
    }

    public IEnumerable<IToolApprovalRequirement> GetRequirements()
    {
        yield return new RequiresVerifiedIdentityRequirement(RequiredLevel);
    }
}
