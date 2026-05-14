using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Default <see cref="IAuthenticationOrchestrator"/>. Runs each registered
/// <see cref="ICallerAuthenticator"/> in DI registration order, recording every attempt on the
/// supplied <see cref="CallerAuthenticationState"/> and short-circuiting on the first
/// <see cref="AuthenticationOutcome.Failed"/> when <see cref="StopOnFailure"/> is true (the default).
/// </summary>
public sealed class AuthenticationOrchestrator : IAuthenticationOrchestrator
{
    private readonly IReadOnlyList<ICallerAuthenticator> _authenticators;
    private readonly ILogger<AuthenticationOrchestrator> _logger;

    public AuthenticationOrchestrator(
        IEnumerable<ICallerAuthenticator> authenticators,
        ILogger<AuthenticationOrchestrator>? logger = null)
    {
        _authenticators = [.. authenticators];
        _logger = logger ?? NullLogger<AuthenticationOrchestrator>.Instance;
    }

    /// <summary>When true, stop running authenticators on first <see cref="AuthenticationOutcome.Failed"/>.</summary>
    public bool StopOnFailure { get; init; } = true;

    /// <summary>When true, stop running authenticators as soon as one returns <see cref="AuthenticationOutcome.NeedsChallenge"/>.</summary>
    public bool StopOnChallenge { get; init; } = true;

    public async Task<AuthenticationRunResult> AuthenticateAsync(
        AuthenticationContext context,
        CallerAuthenticationState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(state);

        if (_authenticators.Count == 0)
        {
            _logger.LogDebug("No ICallerAuthenticator registered; returning anonymous identity for call {CallId}", context.CallId);
            return new AuthenticationRunResult(state.Identity, []);
        }

        var steps = new List<AuthenticationStep>(_authenticators.Count);

        foreach (var authenticator in _authenticators)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AuthenticationOutcome outcome;
            try
            {
                var stepContext = context with { CurrentIdentity = state.Identity };
                outcome = await authenticator.AuthenticateAsync(stepContext, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Authenticator {Name} threw for call {CallId}", authenticator.Name, context.CallId);
                outcome = new AuthenticationOutcome.Failed($"Authenticator '{authenticator.Name}' threw: {ex.Message}");
            }

            var step = new AuthenticationStep(authenticator.Name, outcome, DateTimeOffset.UtcNow);
            steps.Add(step);
            state.RecordStep(step);

            switch (outcome)
            {
                case AuthenticationOutcome.Authenticated authenticated:
                    state.TryPromote(authenticated.Identity);
                    state.SetPendingChallenge(null);
                    break;

                case AuthenticationOutcome.NeedsChallenge needsChallenge:
                    state.SetPendingChallenge(needsChallenge.Challenge);
                    if (StopOnChallenge) { return new AuthenticationRunResult(state.Identity, steps); }
                    break;

                case AuthenticationOutcome.Failed:
                    if (StopOnFailure) { return new AuthenticationRunResult(state.Identity, steps); }
                    break;

                case AuthenticationOutcome.NotApplicable:
                    // Fall through to next authenticator.
                    break;
            }
        }

        return new AuthenticationRunResult(state.Identity, steps);
    }
}
