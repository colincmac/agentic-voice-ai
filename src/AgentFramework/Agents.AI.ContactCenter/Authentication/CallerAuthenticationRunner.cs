using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Call-start helper invoked by every <see cref="IConversationStrategy"/> implementation
/// from <c>StartAsync</c> (before the workflow's initial stage is entered). Runs the
/// per-call <see cref="IAuthenticationOrchestrator"/> chain against the scoped
/// <see cref="CallerAuthenticationState"/> using the caller-edge metadata supplied on the
/// <see cref="StrategyStartContext"/>, then mirrors the resulting authenticator steps onto
/// the strategy's event channel.
/// </summary>
/// <remarks>
/// <para>
/// Event-emission shape is intentionally identical to
/// <see cref="CallerElevationDispatcher.DispatchAsync"/>:
/// <list type="bullet">
///   <item><description><see cref="AuthenticationOutcome.Authenticated"/> → <c>CallerIdentified</c>.</description></item>
///   <item><description><see cref="AuthenticationOutcome.Failed"/> → <c>CallerAuthenticationFailed</c>.</description></item>
///   <item><description><see cref="AuthenticationOutcome.NeedsChallenge"/> → <c>CallerAuthenticationChallenge</c>.</description></item>
/// </list>
/// A single <c>CallerVerificationLevelChanged</c> is emitted at the end when the strongest
/// achieved <see cref="CallerVerificationLevel"/> moves from the pre-run value. This keeps
/// <see cref="ICallObserver"/> consumers (e.g. <c>CallerAuthStateObserver</c>) unable to tell
/// call-start runs from mid-call elevations apart.
/// </para>
/// <para>
/// When the per-call DI scope does not contain an <see cref="IAuthenticationOrchestrator"/>
/// or a <see cref="CallerAuthenticationState"/> (i.e. the host never called
/// <c>AddCallerAuthentication()</c>), the runner is a no-op and returns an empty
/// <see cref="AuthenticationRunResult"/>. Exceptions thrown by the orchestrator are caught
/// and logged so a misbehaving authenticator never blocks the strategy from starting.
/// </para>
/// </remarks>
public static class CallerAuthenticationRunner
{
    /// <summary>
    /// Resolve the orchestrator + state from <paramref name="context"/>.<see cref="StrategyStartContext.Services"/>,
    /// run the chain once with the caller-edge metadata in scope, and mirror the resulting steps
    /// onto <paramref name="events"/>.
    /// </summary>
    /// <param name="context">Per-call strategy startup context. Provides the call id, caller metadata, and DI scope.</param>
    /// <param name="events">Optional writer for <see cref="StrategyEvent"/>s. When non-null the runner emits the same events as <see cref="CallerElevationDispatcher"/>.</param>
    /// <param name="logger">Optional logger. Defaults to <see cref="NullLogger.Instance"/> when omitted.</param>
    /// <param name="cancellationToken">Cancellation token for the orchestrator run.</param>
    /// <returns>The aggregate <see cref="AuthenticationRunResult"/> from the orchestrator, or an empty result when auth is not wired.</returns>
    public static async Task<AuthenticationRunResult> RunAsync(
        StrategyStartContext context,
        IServiceProvider services,
        ChannelWriter<StrategyEvent>? events = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        var orchestrator = services.GetService<IAuthenticationOrchestrator>();
        var state = services.GetService<CallerAuthenticationState>();
        if (orchestrator is null || state is null)
        {
            log.LogDebug(
                "CallerAuthenticationRunner: skipping call-start authentication for call {CallId} (orchestrator={Orchestrator}, state={State}).",
                context.CallId, orchestrator is not null, state is not null);
            return new AuthenticationRunResult(CallerIdentity.Anonymous, []);
        }

        var previousLevel = state.Identity.VerificationLevel;
        var authContext = new AuthenticationContext(
            CallId: context.CallId,
            CallerMetadata: context.CallerMetadata,
            CurrentIdentity: state.Identity,
            Services: services);

        AuthenticationRunResult result;
        try
        {
            result = await orchestrator.AuthenticateAsync(authContext, state, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "CallerAuthenticationRunner: orchestrator threw during call-start authentication for call {CallId}; continuing with current identity '{Identity}'.",
                context.CallId, state.Identity.UserId);
            return new AuthenticationRunResult(state.Identity, []);
        }

        if (events is null)
        {
            return result;
        }

        foreach (var step in result.Steps)
        {
            switch (step.Outcome)
            {
                case AuthenticationOutcome.Authenticated authenticated:
                    await events.WriteAsync(
                        new StrategyEvent.CallerIdentified(authenticated.Identity, step.AuthenticatorName, step.At),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case AuthenticationOutcome.Failed failed:
                    await events.WriteAsync(
                        new StrategyEvent.CallerAuthenticationFailed(step.AuthenticatorName, failed.Reason, step.At),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case AuthenticationOutcome.NeedsChallenge challenge:
                    await events.WriteAsync(
                        new StrategyEvent.CallerAuthenticationChallenge(challenge.Challenge, step.At),
                        cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        if (state.Identity.VerificationLevel != previousLevel)
        {
            await events.WriteAsync(
                new StrategyEvent.CallerVerificationLevelChanged(previousLevel, state.Identity.VerificationLevel, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}
