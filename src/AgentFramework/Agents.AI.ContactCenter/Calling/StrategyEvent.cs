using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Configuration;

namespace Agents.AI.ContactCenter.Calling;

/// <summary>
/// Discriminated event emitted by a strategy. Observers and the session pattern-match
/// on the concrete record.
/// </summary>
public abstract record StrategyEvent(DateTimeOffset At)
{

    public sealed record Transcript(string Speaker, string Text, bool IsFinal, DateTimeOffset At) : StrategyEvent(At);
    public sealed record AgentUtterance(string AgentId, string Text, DateTimeOffset At) : StrategyEvent(At);
    public sealed record AudioPlayed(string AudioFileId, DateTimeOffset At) : StrategyEvent(At);
    public sealed record AgentSpeakingChanged(string AgentId, string? AgentDisplayName, DateTimeOffset At) : StrategyEvent(At);
    public sealed record DelegateInsight(string AgentId, string Insight, double? Confidence, DateTimeOffset At) : StrategyEvent(At);
    public sealed record FunctionCalled(string Name, IReadOnlyDictionary<string, object?> Arguments, DateTimeOffset At) : StrategyEvent(At);
    public sealed record DtmfRecognized(string Digits, string? StepId, DateTimeOffset At) : StrategyEvent(At);
    public sealed record WorkflowStepEntered(string StepId, DateTimeOffset At) : StrategyEvent(At);
    public sealed record IntentClassified(string Intent, double Confidence, DateTimeOffset At) : StrategyEvent(At);
    public sealed record EscalationRequested(string Reason, DateTimeOffset At) : StrategyEvent(At);
    public sealed record TierDegraded(AgentTier From, AgentTier To, string Reason, DateTimeOffset At) : StrategyEvent(At);
    public sealed record Faulted(string Message, Exception? Exception, DateTimeOffset At) : StrategyEvent(At);

    /// <summary>Caller's identity was established or elevated by an <see cref="ICallerAuthenticator"/>.</summary>
    public sealed record CallerIdentified(CallerIdentity Identity, string AuthenticatorName, DateTimeOffset At) : StrategyEvent(At);

    /// <summary>An authenticator attempted verification and the caller failed.</summary>
    public sealed record CallerAuthenticationFailed(string AuthenticatorName, string Reason, DateTimeOffset At) : StrategyEvent(At);

    /// <summary>An authenticator requires caller interaction to complete (OTP, biometric phrase, …).</summary>
    public sealed record CallerAuthenticationChallenge(AuthenticationChallenge Challenge, DateTimeOffset At) : StrategyEvent(At);

    /// <summary>The caller's strongest verification level changed.</summary>
    public sealed record CallerVerificationLevelChanged(CallerVerificationLevel From, CallerVerificationLevel To, DateTimeOffset At) : StrategyEvent(At);

    /// <summary>
    /// Emitted by the session when the active edge dropped a directive whose kind
    /// is not in its <see cref="EdgeCapabilities"/>. Indicates a strategy/edge
    /// mismatch — typically a strategy paired with the wrong tier of edge.
    /// </summary>
    public sealed record DispatchUnsupported(string DirectiveKind, EdgeCapabilities EdgeCapabilities, DateTimeOffset At) : StrategyEvent(At);
}
