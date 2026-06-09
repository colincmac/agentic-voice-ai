using Agents.AI.Extensions.ToolApproval;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Method-level attribute that declares a minimum <see cref="CallerVerificationLevel"/> the
/// caller must have achieved before the tool can execute. Enforced at tool-invocation time
/// by <see cref="IvrWorkflow.Authorization.CallerVerificationFilter"/>, which reads the
/// attribute via reflection from the underlying method and compares the requirement against
/// the per-call <see cref="CallerAuthenticationState"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresCallerVerificationAttribute : Attribute, IToolApprovalRequirementData
{
    public RequiresCallerVerificationAttribute(CallerVerificationLevel minimumLevel, string? failureMessage = null)
    {
        MinimumLevel = minimumLevel;
        FailureMessage = failureMessage;
    }
    public CallerVerificationLevel MinimumLevel { get; set; }
    public string? FailureMessage { get; set; }
    public IEnumerable<IToolApprovalRequirement> GetRequirements() => [new RequiresCallerVerificationRequirement(MinimumLevel, FailureMessage)];

}

public sealed class RequiresCallerVerificationRequirement : IToolApprovalRequirement
{
    public RequiresCallerVerificationRequirement(CallerVerificationLevel minimumLevel, string? failureMessage = null)
    {
        MinimumLevel = minimumLevel;
        FailureMessage = failureMessage;
    }

    public CallerVerificationLevel MinimumLevel { get; }
    public string? FailureMessage { get; }

    /// <summary>
    /// Optional human-readable explanation surfaced as the failure response when the caller's
    /// level is below <see cref="MinimumLevel"/>. Defaults to a generic message describing
    /// the required level.
    /// </summary>
    public AIContent? OnFailureResponse  => new TextContent(FailureMessage ?? $"Caller must have achieved at least {MinimumLevel} verification level to execute this action.");
}

public sealed class RequiresCallerVerificationHandler(ILogger<RequiresCallerVerificationHandler> logger) : ToolApprovalHandler<RequiresCallerVerificationRequirement>
{
    private readonly ILogger<RequiresCallerVerificationHandler> _logger = logger;
    protected override Task HandleRequirementAsync(
            ToolApprovalContext context,
            RequiresCallerVerificationRequirement requirement)
    {
        var state = ResolveAuthenticationState(context.Arguments, context.InvokingAgent);
        if (state is null)
        {
            _logger?.LogWarning(
                "CallerVerificationFilter: no CallerAuthenticationState resolved for tool '{Tool}' (required level '{Level}'); failing closed.",
                context.Tool.Name, requirement.MinimumLevel);
            context.Fail(requirement);
            return Task.CompletedTask;
        }

        var current = state.Identity.VerificationLevel;
        if (current >= requirement.MinimumLevel)
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(requirement);
        }
        return Task.CompletedTask;
    }

    private static CallerAuthenticationState? ResolveAuthenticationState(AIFunctionArguments arguments, AIAgent? agent) =>
    arguments.Services?.GetService<CallerAuthenticationState>()
    ?? agent?.GetService(typeof(CallerAuthenticationState)) as CallerAuthenticationState;

}
