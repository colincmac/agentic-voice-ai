using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.RealtimeVoice.Azure.Configuration;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed;

// SKETCH — the call container.
//
// Replaces ContactCenterConversationHub + ContactCenterConversationSession +
// HubSessionParticipant + HubSessionContext + HubSessionEventBus +
// TieredSessionActivator + IContactCenterConversationSessionActivator + the
// IServiceScope nesting.
//
// One CallSession per active phone call:
//   - exactly one caller ICallEdge
//   - exactly one IConversationStrategy (the brain)
//   - 0..1 supervisor ICallEdge (barge-in)
//   - 0..N ICallObservers (analytics, recording, dashboard, A2A side-cars)
//
// The session is the only thing that knows how to wire those four together.
// It pumps caller audio → strategy.InboundAudio, strategy.OutboundAudio →
// caller edge, and broadcasts StrategyEvents to observers.

/// <summary>One active IVR call.</summary>
public interface ICallSession : IAsyncDisposable
{
    string CallId { get; }

    CallSessionState State { get; }

    DateTimeOffset StartedAt { get; }

    ICallEdge CallerEdge { get; }

    IConversationStrategy Strategy { get; }

    ICallEdge? SupervisorEdge { get; }

    SupervisorMode? SupervisorMode { get; }

    IReadOnlyList<ICallObserver> Observers { get; }

    /// <summary>Connect the edge, start the strategy, and start observers.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Attach a supervisor edge for monitor / whisper / barge-in.</summary>
    Task<bool> AttachSupervisorAsync(
        ICallEdge supervisorEdge,
        SupervisorMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>Change supervisor's mode without re-attaching (Monitor → BargeIn → Monitor).</summary>
    Task<bool> ChangeSupervisorModeAsync(SupervisorMode mode, CancellationToken cancellationToken = default);

    Task DetachSupervisorAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Swap the strategy mid-call. Used by the composite fallback orchestrator and
    /// by tests. Workflow state is preserved automatically.
    /// </summary>
    Task<bool> ReplaceStrategyAsync(IConversationStrategy newStrategy, CancellationToken cancellationToken = default);

    /// <summary>Initiate transfer (TPE → Dynamics CCaaS). Strategy stops, edge survives until ACS confirms.</summary>
    Task TransferAsync(TransferRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hang up the call leg through the caller edge's platform call-control surface
    /// (ACS Call Automation today). This is the AI-callable / supervisor-callable
    /// equivalent of the caller hanging up. Implementations should also tear down
    /// local session resources (equivalent to <see cref="EndAsync(string?, CancellationToken)"/>).
    /// </summary>
    /// <param name="hangUpForEveryone">When <see langword="true"/>, ends the call for all parties.</param>
    /// <param name="reason">Optional human-readable reason recorded in telemetry.</param>
    Task HangUpAsync(bool hangUpForEveryone = true, string? reason = null, CancellationToken cancellationToken = default);

    Task EndAsync(string? reason = null, CancellationToken cancellationToken = default);

    event Func<CallSessionState, ValueTask>? StateChanged;
}

public enum CallSessionState
{
    Created,
    Connecting,
    Active,
    Suspended,           // strategy paused (e.g., supervisor BargeIn)
    Transferring,
    Ending,
    Ended,
    Faulted
}

public enum SupervisorMode
{
    /// <summary>Listen-only. Strategy continues. Supervisor hears caller + agent audio.</summary>
    Monitor,

    /// <summary>Whisper to the AI ensemble (or human agent later) without the caller hearing.
    /// For AI calls, supervisor speech is fed to the ensemble as an out-of-band system message.</summary>
    Whisper,

    /// <summary>Take over speaker role. Strategy is suspended; supervisor audio bridges to caller.</summary>
    BargeIn
}

/// <summary>
/// Factory used by the call automation handler when an IncomingCall event arrives.
/// Replaces today's <c>IContactCenterConversationSessionActivator</c> + hub indirection.
/// Resolves the strategy synchronously so no caller audio can arrive before the brain is wired.
/// </summary>
public interface ICallSessionFactory
{
    Task<ICallSession> CreateAsync(CallSessionRequest request, CancellationToken cancellationToken = default);
}

public sealed record CallSessionRequest
{
    public required string CallId { get; init; }
    public required ICallEdge CallerEdge { get; init; }
    public required RealtimeIvrWorkflowDefinition Workflow { get; init; }

    /// <summary>Override tier resolution. When null, the registered <c>IAgentTierResolver</c> picks.</summary>
    public AgentTier? PreferredTier { get; init; }

    /// <summary>Observers to start with the call. Defaults from DI are added on top.</summary>
    public IReadOnlyList<ICallObserver>? Observers { get; init; }
}

/// <summary>
/// Live registry of active call sessions. Replaces the dictionary on
/// ContactCenterConversationHub plus its IHostedService surface.
/// </summary>
public interface ICallSessionRegistry
{
    ICallSession? TryGet(string callId);

    IReadOnlyCollection<ICallSession> ActiveSessions { get; }

    Task<bool> RemoveAsync(string callId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds an <see cref="IConversationStrategy"/> for a specific <see cref="AgentTier"/>.
/// One factory per tier, registered in DI. Replaces today's <c>IAgentTransportFactory</c>.
/// </summary>
public interface IConversationStrategyFactory
{
    AgentTier Tier { get; }

    ValueTask<IConversationStrategy> CreateAsync(
        string callId,
        IServiceProvider services,
        RealtimeIvrWorkflowDefinition workflow,
        IvrWorkflowState? restoreFrom,
        CancellationToken cancellationToken = default);
}

public sealed record TransferRequest(
    string TargetIdentifier,                            // E.164 / Teams ID / ACS user
    TransferKind Kind,
    IReadOnlyDictionary<string, string>? CustomContext);

public enum TransferKind
{
    BlindToPhoneNumber,
    BlindToTeamsUser,
    Consultative
}
