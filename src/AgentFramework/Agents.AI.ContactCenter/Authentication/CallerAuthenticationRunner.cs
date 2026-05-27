using System.Threading.Channels;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Agents.AI.ContactCenter.Calling;

namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Shared helper that runs the registered <see cref="IAuthenticationOrchestrator"/> for a
/// strategy at call start, projects each authenticator step into a <see cref="StrategyEvent"/>
/// on the supplied writer, and returns a <see cref="ConversationContext"/> populated with the
/// resolved caller identity. Used by every <see cref="IConversationStrategy"/> that wants to
/// surface caller authentication identically (Realtime, DTMF streaming, DTMF verb, …).
/// </summary>
public static class CallerAuthenticationRunner
{
    /// <summary>
    /// Resolve the orchestrator from <paramref name="services"/>, run it once, and emit
    /// per-step <see cref="StrategyEvent"/>s on <paramref name="events"/>.
    /// </summary>
    /// <param name="services">Per-call DI scope.</param>
    /// <param name="callId">Call identifier (typically the ACS connection id).</param>
    /// <param name="callerMetadata">
    /// Caller-edge metadata. When <see langword="null"/> (e.g. the strategy was prewarmed before
    /// the edge attached) authentication is skipped and an empty <see cref="ConversationContext"/>
    /// is returned.
    /// </param>
    /// <param name="events">Channel writer the strategy uses to surface events to the call session.</param>
    /// <param name="workflowState">
    /// Optional. When supplied, the resolved <see cref="CallerVerificationLevel"/> is also written to
    /// <see cref="IvrWorkflowState"/> so step transitions / guards that read it pick up the value.
    /// </param>
    /// <param name="logger">Logger. Required.</param>
    /// <param name="cancellationToken">Token observed throughout.</param>
    /// <returns>
    /// A <see cref="ConversationContext"/> with caller name / id / verification level filled in (using the
    /// instance registered in DI when present, otherwise a new instance). Strategies forward this
    /// to <see cref="IIvrWorkflowNavigator.BuildCurrentStepPrompt"/> so prompts can address the
    /// caller by name.
    /// </returns>
    public static async Task<ConversationContext> RunAsync(
        IServiceProvider services,
        string callId,
        CallEdgeMetadata? callerMetadata,
        ChannelWriter<StrategyEvent> events,
        ILogger logger,
        IvrWorkflowState? workflowState = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(logger);

        var orchestrator = services.GetService<IAuthenticationOrchestrator>();
        var state = services.GetService<CallerAuthenticationState>();
        if (orchestrator is null || state is null)
        {
            return BuildConversationContext(services, CallerIdentity.Anonymous);
        }

        if (callerMetadata is null)
        {
            logger.LogDebug("Skipping caller authentication: no caller metadata for call {CallId}", callId);
            return BuildConversationContext(services, state.Identity);
        }

        var telemetry = services.GetRequiredService<CallingTelemetry>();

        using var authSpan = telemetry.StartChildActivity("contact_center.strategy.authenticate", callId);

        var previousLevel = state.Identity.VerificationLevel;
        var tags = workflowState?.CurrentStepName is { Length: > 0 } stepName
            ? (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["ivr.step"] = stepName }
            : null;
        var context = new AuthenticationContext(
            CallId: callId,
            CallerMetadata: callerMetadata,
            CurrentIdentity: state.Identity,
            Services: services,
            Tags: tags);

        AuthenticationRunResult result;
        try
        {
            result = await orchestrator.AuthenticateAsync(context, state, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            CallingActivitySource.SetError(authSpan, ex);
            logger.LogWarning(ex, "Caller authentication threw for call {CallId}", callId);
            await events.WriteAsync(
                new StrategyEvent.CallerAuthenticationFailed("(orchestrator)", ex.Message, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
            return BuildConversationContext(services, state.Identity);
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

        if (workflowState is not null)
        {
            workflowState.SetVerificationLevel(state.Identity.VerificationLevel);
        }

        authSpan?.SetTag("caller.verification_level", state.Identity.VerificationLevel.ToString());
        authSpan?.SetTag("caller.user_id", state.Identity.UserId);

        return BuildConversationContext(services, state.Identity);
    }

    private static ConversationContext BuildConversationContext(IServiceProvider services, CallerIdentity identity)
    {
        var context = services.GetService<ConversationContext>() ?? new ConversationContext();
        context.CallerName = identity.DisplayName;
        context.CallerId = identity.UserId;
        context.VerificationLevel = identity.VerificationLevel;
        return context;
    }
}
