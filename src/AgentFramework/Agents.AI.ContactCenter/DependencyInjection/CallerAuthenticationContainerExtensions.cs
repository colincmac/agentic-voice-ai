using Agents.AI.ContactCenter.Authentication;
using Agents.AI.Extensions.AITools;
using Agents.AI.Extensions.ToolApproval;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agents.AI.ContactCenter.DependencyInjection;

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
        services.TryAddScoped<ICallerElevationDispatcher, CallerElevationDispatcher>();
        services.TryAddSingleton<IChallengeStore, InMemoryChallengeStore>();

        // Always-present fallback so the orchestrator never enumerates an empty list.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICallerAuthenticator, AnonymousCallerAuthenticator>());

        // Per-tool gating: the ToolApproval handler that evaluates [RequiresCallerVerification(level)]
        // attributes against the per-call CallerAuthenticationState.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolApprovalHandler, CallerVerificationApprovalHandler>());

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
    /// Registers the <see cref="PinAuthenticator"/> together with the supplied
    /// <typeparamref name="TPinValidator"/> and a per-call <see cref="PinAttempt"/>. Tools
    /// (DTMF collectors, realtime function calls, etc.) set <see cref="PinAttempt.Digits"/>
    /// and then invoke <see cref="IAuthenticationOrchestrator"/> to elevate the caller to
    /// <see cref="CallerVerificationLevel.KnowledgeBased"/>.
    /// </summary>
    public static CallSessionContainerBuilder AddPinAuthenticator<TPinValidator>(
        this CallSessionContainerBuilder builder,
        ServiceLifetime validatorLifetime = ServiceLifetime.Singleton)
        where TPinValidator : class, IPinValidator
    {
        builder.Services.TryAdd(ServiceDescriptor.Describe(typeof(IPinValidator), typeof(TPinValidator), validatorLifetime));
        builder.Services.TryAddScoped<PinAttempt>();
        return builder.AddCallerAuthenticator<PinAuthenticator>();
    }

    /// <summary>
    /// Registers the canonical <see cref="CallerAuthenticationTools"/> as a scoped
    /// <see cref="IAIToolCollection"/> so the realtime / chat agent picks them up automatically.
    /// </summary>
    /// <remarks>
    /// Tools self-gate on what's registered: <c>validate-pin</c> only surfaces when a
    /// <see cref="PinAttempt"/> is registered (i.e. you called <see cref="AddPinAuthenticator{T}"/>);
    /// <c>request-sms-otp</c>/<c>submit-sms-otp</c> only surface when a <see cref="SmsOtpAttempt"/>
    /// is registered. Call this after the authenticators you want to expose.
    /// </remarks>
    public static CallSessionContainerBuilder AddCallerAuthenticationTools(this CallSessionContainerBuilder builder)
    {
        // SmsOtpAttempt is registered here too so the OTP authenticator's scoped state
        // is available when AddCallerAuthenticator<SmsOtpAuthenticator>() is used directly.
        builder.Services.TryAddScoped<SmsOtpAttempt>();
        builder.Services.AddScoped<IAIToolCollection, CallerAuthenticationTools>();
        return builder;
    }
}

