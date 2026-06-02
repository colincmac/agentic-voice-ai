namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Method-level attribute that declares a minimum <see cref="CallerVerificationLevel"/> the
/// caller must have achieved before the tool can execute. Enforced at tool-invocation time
/// by <see cref="IvrWorkflow.Authorization.CallerVerificationFilter"/>, which reads the
/// attribute via reflection from the underlying method and compares the requirement against
/// the per-call <see cref="CallerAuthenticationState"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresCallerVerificationAttribute : Attribute
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
}
