using Agents.AI.Extensions.ToolApproval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Method-level attribute that declares a minimum <see cref="CallerVerificationLevel"/> the
/// caller must have achieved before the tool can execute. Discovered by the existing
/// <c>AuthorizingAgentFunction</c> in <c>Agents.AI.Extensions.RealtimeAgentHelpers</c> and
/// evaluated by <see cref="CallerVerificationApprovalHandler"/> as part of the standard
/// <see cref="IToolApprovalRequirement"/> pipeline.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresCallerVerificationAttribute : Attribute, IToolApprovalRequirementData
{
    public RequiresCallerVerificationAttribute(CallerVerificationLevel minimumLevel)
    {
        MinimumLevel = minimumLevel;
    }

    public CallerVerificationLevel MinimumLevel { get; }

    /// <summary>
    /// Optional human-readable explanation surfaced as the failure response when the caller's
    /// level is below <see cref="MinimumLevel"/>. Defaults to a generic message describing
    /// the required level.
    /// </summary>
    public string? FailureMessage { get; set; }

    public IEnumerable<IToolApprovalRequirement> GetRequirements() => [
        new RequiresCallerVerificationRequirement(MinimumLevel, FailureMessage)
    ];
}

/// <summary>
/// Requirement evaluated by <see cref="CallerVerificationApprovalHandler"/> — succeeds when
/// the scoped <see cref="CallerAuthenticationState.Identity"/> verification level is
/// greater than or equal to <see cref="MinimumLevel"/>.
/// </summary>
public sealed class RequiresCallerVerificationRequirement : IToolApprovalRequirement
{
    public RequiresCallerVerificationRequirement(CallerVerificationLevel minimumLevel, string? failureMessage = null)
    {
        MinimumLevel = minimumLevel;
        FailureMessage = failureMessage;
    }

    public CallerVerificationLevel MinimumLevel { get; }
    public string? FailureMessage { get; }

    public AIContent? OnFailureResponse => new TextContent(
        FailureMessage
        ?? $"This action requires the caller to be verified at level '{MinimumLevel}' or higher.");
}

/// <summary>
/// Resolves the per-call <see cref="CallerAuthenticationState"/> through the tool-invocation
/// service provider and either <see cref="ToolApprovalContext.Succeed"/>s or
/// <see cref="ToolApprovalContext.Fail"/>s each <see cref="RequiresCallerVerificationRequirement"/>.
/// </summary>
public sealed class CallerVerificationApprovalHandler : ToolApprovalHandler<RequiresCallerVerificationRequirement>
{
    private readonly ILogger<CallerVerificationApprovalHandler> _logger;

    public CallerVerificationApprovalHandler(ILogger<CallerVerificationApprovalHandler>? logger = null)
    {
        _logger = logger ?? NullLogger<CallerVerificationApprovalHandler>.Instance;
    }

    protected override Task HandleRequirementAsync(
        ToolApprovalContext context,
        RequiresCallerVerificationRequirement requirement)
    {
        // Mirror the lookup pattern used by RequiresAIToolScopes / the other built-in
        // handlers: walk the tool's GetService chain (covers AIFunctionArguments.Services
        // and the wrapping agent's service provider).
        var state = context.Tool.GetService(typeof(CallerAuthenticationState)) as CallerAuthenticationState
                    ?? context.InvokingAgent.GetService(typeof(CallerAuthenticationState)) as CallerAuthenticationState;

        if (state is null)
        {
            _logger.LogWarning(
                "RequiresCallerVerification handler could not resolve CallerAuthenticationState for tool '{Tool}'; failing closed.",
                context.Tool.Name);
            context.Fail(requirement);
            return Task.CompletedTask;
        }

        var currentLevel = state.Identity.VerificationLevel;
        if (currentLevel >= requirement.MinimumLevel)
        {
            context.Succeed(requirement);
        }
        else
        {
            _logger.LogInformation(
                "Tool '{Tool}' denied: caller verification level {Current} < required {Required}.",
                context.Tool.Name, currentLevel, requirement.MinimumLevel);
            context.Fail(requirement);
        }

        return Task.CompletedTask;
    }
}
