using Agents.AI.RealtimeVoice.Azure.Authorization;
using Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed;

/// <summary>
/// DI extensions for plugging caller-authentication methods into the
/// <see cref="CallSessionContainerBuilder"/> pipeline.
/// </summary>
public static class CallerAuthenticationContainerExtensions
{
    /// <summary>
    /// Registers the per-call <see cref="CallerAuthenticationState"/> store, the default
    /// <see cref="AuthenticationOrchestrator"/>, and a <see cref="AnonymousCallerAuthenticator"/>
    /// fallback. Strategies (e.g. <c>RealtimeVoiceStrategy</c>) automatically pick up the
    /// orchestrator from DI when present.
    /// </summary>
    /// <remarks>
    /// Adding concrete authenticators is done by chaining
    /// <see cref="AddCallerAuthenticator{TAuthenticator}"/> after this method.
    /// </remarks>
    public static CallSessionContainerBuilder AddCallerAuthentication(this CallSessionContainerBuilder builder)
    {
        var services = builder.Services;

        services.TryAddScoped<CallerAuthenticationState>();
        services.TryAddScoped<IAuthenticationOrchestrator, AuthenticationOrchestrator>();

        // Always-present fallback so the orchestrator never enumerates an empty list.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICallerAuthenticator, AnonymousCallerAuthenticator>());

        return builder;
    }

    /// <summary>Adds an <see cref="ICallerAuthenticator"/> implementation to the chain.</summary>
    /// <remarks>
    /// Authenticators run in DI registration order. Register stronger / more-expensive
    /// authenticators after passive ones (e.g. ANI lookup → MFA → voice biometric).
    /// </remarks>
    public static CallSessionContainerBuilder AddCallerAuthenticator<TAuthenticator>(
        this CallSessionContainerBuilder builder,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TAuthenticator : class, ICallerAuthenticator
    {
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Describe(typeof(ICallerAuthenticator), typeof(TAuthenticator), lifetime));
        return builder;
    }

    /// <summary>
    /// Adds the ANI-based <see cref="AniIdentityLookupAuthenticator"/> backed by the
    /// supplied <see cref="ICallerDirectory"/> implementation. Convenience over the
    /// two-step "register directory + add authenticator" call.
    /// </summary>
    public static CallSessionContainerBuilder AddAniIdentityLookupAuthenticator<TDirectory>(
        this CallSessionContainerBuilder builder,
        ServiceLifetime directoryLifetime = ServiceLifetime.Singleton)
        where TDirectory : class, ICallerDirectory
    {
        builder.Services.TryAdd(ServiceDescriptor.Describe(typeof(ICallerDirectory), typeof(TDirectory), directoryLifetime));
        return builder.AddCallerAuthenticator<AniIdentityLookupAuthenticator>();
    }

    /// <summary>
    /// Adapter overload that bridges the existing <see cref="IUserIdentityService"/> to the
    /// new <see cref="ICallerDirectory"/> contract. Use this when migrating a codebase that
    /// already registers <see cref="InMemoryUserIdentityService"/> or another implementation.
    /// </summary>
    public static CallSessionContainerBuilder AddAniIdentityLookupAuthenticatorFromUserIdentityService(
        this CallSessionContainerBuilder builder)
    {
        builder.Services.TryAddSingleton<ICallerDirectory, UserIdentityServiceCallerDirectoryAdapter>();
        return builder.AddCallerAuthenticator<AniIdentityLookupAuthenticator>();
    }
}
