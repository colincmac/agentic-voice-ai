using Agents.AI.ContactCenter.IvrWorkflow;

namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// IVR step guard that succeeds only when the caller's current
/// <see cref="CallerVerificationLevel"/> meets or exceeds <see cref="MinimumLevel"/>.
/// </summary>
/// <remarks>
/// The guard reads the verification level off <see cref="IvrWorkflowState.VerificationLevel"/>,
/// which <see cref="CallerAuthenticationRunner"/> and <see cref="ICallerElevationDispatcher"/>
/// mirror from the per-call <see cref="CallerAuthenticationState"/>. Using the workflow-state
/// property keeps the <see cref="IIvrStepGuard"/> signature unchanged while still routing
/// every promotion through the authentication framework.
/// </remarks>
public sealed class MinimumVerificationGuard : IIvrStepGuard
{
    private readonly CallerVerificationLevel _minimum;
    private readonly string _failureMessage;

    public MinimumVerificationGuard(CallerVerificationLevel minimum, string? failureMessage = null)
    {
        _minimum = minimum;
        _failureMessage = failureMessage
            ?? $"Caller verification level must be at least '{minimum}' to enter this step.";
    }

    public CallerVerificationLevel MinimumLevel => _minimum;

    public Task<IvrGuardResult> EvaluateAsync(IvrWorkflowState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var result = state.VerificationLevel >= _minimum
            ? IvrGuardResult.Pass()
            : IvrGuardResult.Fail(_failureMessage);
        return Task.FromResult(result);
    }
}
