using System.Collections.Generic;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Authentication;

/// <summary>
/// Input passed to <see cref="ICallerAuthenticator.AuthenticateAsync"/>. Carries everything an
/// authenticator needs to make a decision without coupling to the strategy or the call session.
/// </summary>
/// <param name="CallId">Active call identifier (typically the ACS call connection id).</param>
/// <param name="CallerMetadata">Snapshot of the caller-edge metadata (E.164, display name, server call id).</param>
/// <param name="CurrentIdentity">
/// The identity the orchestrator has built so far. <see cref="CallerIdentity.Anonymous"/> on the
/// first attempt; subsequent authenticators see whatever previous ones produced and may use it
/// to skip work or to elevate the verification level.
/// </param>
/// <param name="Services">Per-call DI scope. Authenticators should resolve dependencies from here.</param>
/// <param name="Tags">Free-form tags supplied by the caller of the orchestrator (e.g. workflow step name).</param>
public sealed record AuthenticationContext(
    string CallId,
    CallEdgeMetadata CallerMetadata,
    CallerIdentity CurrentIdentity,
    IServiceProvider Services,
    IReadOnlyDictionary<string, string>? Tags = null);
