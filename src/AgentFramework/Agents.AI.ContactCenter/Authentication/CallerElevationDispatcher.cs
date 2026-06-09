using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Default <see cref="ICallerElevationDispatcher"/>. Looks up the named authenticator from
/// the DI-registered <see cref="ICallerAuthenticator"/> enumerable, builds a one-authenticator
/// <see cref="AuthenticationOrchestrator"/> for the run (so failure / challenge handling and
/// state recording stay identical to the call-start chain), and projects each step into the
/// supplied strategy event channel.
/// </summary>
public sealed class CallerElevationDispatcher : ICallerElevationDispatcher
{
    private readonly IReadOnlyDictionary<string, ICallerAuthenticator> _authenticatorsByName;
    private readonly CallerAuthenticationState _state;
    private readonly IServiceProvider _services;
    private readonly ILogger<CallerElevationDispatcher> _logger;

    public CallerElevationDispatcher(
        IEnumerable<ICallerAuthenticator> authenticators,
        CallerAuthenticationState state,
        IServiceProvider services,
        ILogger<CallerElevationDispatcher>? logger = null)
    {
        _state = state;
        _services = services;
        _logger = logger ?? NullLogger<CallerElevationDispatcher>.Instance;
        _authenticatorsByName = authenticators
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<AuthenticationRunResult> DispatchAsync(
        string authenticatorName,
        string callId,
        CallEdgeMetadata? callerMetadata = null,
        ChannelWriter<StrategyEvent>? events = null,
        IReadOnlyDictionary<string, string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(authenticatorName);
        ArgumentException.ThrowIfNullOrEmpty(callId);

        if (!_authenticatorsByName.TryGetValue(authenticatorName, out var authenticator))
        {
            _logger.LogWarning(
                "No ICallerAuthenticator named '{Name}' is registered for call {CallId}; dispatch is a no-op.",
                authenticatorName, callId);
            return new AuthenticationRunResult(_state.Identity, []);
        }

        var previousLevel = _state.Identity.VerificationLevel;

        var context = new AuthenticationContext(
            CallId: callId,
            CallerMetadata: callerMetadata,
            CurrentIdentity: _state.Identity,
            Services: _services,
            Tags: tags);

        AuthenticationOutcome outcome;
        try
        {
            outcome = await authenticator.AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Authenticator {Name} threw during elevation dispatch for call {CallId}", authenticator.Name, callId);
            outcome = new AuthenticationOutcome.Failed($"Authenticator '{authenticator.Name}' threw: {ex.Message}");
        }

        var step = new AuthenticationStep(authenticator.Name, outcome, DateTimeOffset.UtcNow);
        _state.RecordStep(step);

        switch (outcome)
        {
            case AuthenticationOutcome.Authenticated authenticated:
                _state.TryPromote(authenticated.Identity);
                _state.SetPendingChallenge(null);
                if (events is not null)
                {
                    await events.WriteAsync(
                        new StrategyEvent.CallerIdentified(authenticated.Identity, authenticator.Name, step.At),
                        cancellationToken).ConfigureAwait(false);
                }
                break;

            case AuthenticationOutcome.Failed failed:
                if (events is not null)
                {
                    await events.WriteAsync(
                        new StrategyEvent.CallerAuthenticationFailed(authenticator.Name, failed.Reason, step.At),
                        cancellationToken).ConfigureAwait(false);
                }
                break;

            case AuthenticationOutcome.NeedsChallenge challenge:
                _state.SetPendingChallenge(challenge.Challenge);
                if (events is not null)
                {
                    await events.WriteAsync(
                        new StrategyEvent.CallerAuthenticationChallenge(challenge.Challenge, step.At),
                        cancellationToken).ConfigureAwait(false);
                }
                break;
        }

        if (events is not null && _state.Identity.VerificationLevel != previousLevel)
        {
            await events.WriteAsync(
                new StrategyEvent.CallerVerificationLevelChanged(previousLevel, _state.Identity.VerificationLevel, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }

        return new AuthenticationRunResult(_state.Identity, [step]);
    }
}
