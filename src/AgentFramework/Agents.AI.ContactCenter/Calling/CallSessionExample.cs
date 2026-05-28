using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Configuration;

namespace Agents.AI.ContactCenter.Calling;

// SKETCH — the wire-up surface, showing how the ACS webhook handler composes a call.
//
// Intent of this file is to make the "answer a call" path readable in 20 lines so
// reviewers can judge whether the contracts above hang together. No implementation;
// the methods would live on a default ICallSessionFactory.

/// <summary>
/// Illustrative pseudo-implementation of the call-answer path. Not compiled logic —
/// kept here so the contract review can see the call site.
/// </summary>
internal static class CallAnswerExample
{
    /*
    // Inside the ACS IncomingCall webhook handler, after AnswerCall succeeds:

    public async Task OnIncomingCallAsync(
        IncomingCallEventData incoming,
        CallConnection callConnection,
        WebSocket mediaWebSocket,
        ICallSessionFactory factory,
        RealtimeIvrWorkflowDefinition workflow,
        CancellationToken ct)
    {
        var callerEdge = new AcsCallerEdge(mediaWebSocket, callConnection.GetCallConnectionProperties(), ct);

        var session = await factory.CreateAsync(new CallSessionRequest
        {
            CallId    = callConnection.CallConnectionId,
            CallerEdge = callerEdge,
            Workflow  = workflow,
            // PreferredTier omitted → tier resolver chooses based on capacity
        }, ct);

        await session.StartAsync(ct);
        // Session keeps itself alive via the registry until the caller hangs up.
    }

    // Supervisor barge-in from the operator dashboard:

    public async Task OnSupervisorJoinAsync(
        string callId,
        SupervisorMode mode,
        WebSocket supervisorWebSocket,
        ICallSessionRegistry registry,
        CancellationToken ct)
    {
        var session = registry.TryGet(callId)
            ?? throw new InvalidOperationException("Call no longer active");

        var supervisorEdge = new SignalRSupervisorEdge(supervisorWebSocket, ct);
        await session.AttachSupervisorAsync(supervisorEdge, mode, ct);
    }

    // Inside the default ICallSessionFactory:

    public async Task<ICallSession> CreateAsync(CallSessionRequest req, CancellationToken ct)
    {
        var scope    = _scopeFactory.CreateAsyncScope();
        var sp       = scope.ServiceProvider;

        var tier      = req.PreferredTier ?? await _tierResolver.ResolveAsync(ct);
        var primary   = await _strategyFactories[tier].CreateAsync(req.CallId, sp, req.Workflow, ct);

        // Wrap in composite if mid-call degradation is enabled — fallback orchestrator
        // becomes part of the strategy graph, not a side-channel.
        IConversationStrategy strategy = _options.AllowMidCallDegradation
            ? new CompositeFallbackStrategy(primary, _strategyFactories, _tierResolver, _options.FallbackOrder)
            : primary;

        var observers = (req.Observers ?? []).Concat(_defaultObservers).ToList();

        return new CallSession(
            req.CallId,
            req.CallerEdge,
            strategy,
            observers,
            _qualityReporter,
            scope,
            _registry,
            _logger);
    }
    */
}

// =============================================================================
// SKETCH — the four "how do I add a strategy" knobs the reviewer should focus on:
// =============================================================================
//
// 1. Strategy factories (replaces today's IAgentTransportFactory):
//
//      public interface IConversationStrategyFactory
//      {
//          AgentTier Tier { get; }
//          ValueTask<IConversationStrategy> CreateAsync(
//              string callId,
//              IServiceProvider services,
//              RealtimeIvrWorkflowDefinition workflow,
//              IvrWorkflowState? restoreFrom,
//              CancellationToken ct);
//      }
//
// 2. Tier resolver (mostly unchanged from today's IAgentTierResolver):
//
//      public interface ITierResolver
//      {
//          ValueTask<AgentTier> ResolveAsync(CancellationToken ct);
//          ValueTask<AgentTier?> ResolveFallbackAsync(AgentTier failedTier, CancellationToken ct);
//          void Acquire(AgentTier tier);
//          void Release(AgentTier tier);
//      }
//
// 3. CompositeFallbackStrategy (replaces FallbackOrchestrator):
//
//      Wraps an inner IConversationStrategy. On the inner emitting StrategyEvent.Faulted
//      or its OutboundAudio channel completing unexpectedly, it:
//        a) snapshots inner.WorkflowState,
//        b) asks the tier resolver for the next tier,
//        c) constructs the next strategy with that workflow state via RestoreFrom,
//        d) replays a "one moment, reconnecting" prompt,
//        e) swaps inner. Caller's edge is untouched.
//      Emits StrategyEvent.TierDegraded so observers know.
//
// 4. Builder (replaces ConversationHubBuilder):
//
//      services
//          .AddCallSessionContainer()
//          .UseTier(AgentTier.RealtimeVoice, sp => new RealtimeVoiceStrategyFactory(...))
//          .UseTier(AgentTier.SttTts,        sp => new SttTtsStrategyFactory(...))
//          .UseTier(AgentTier.Nlu,           sp => new NluStrategyFactory(...))
//          .UseTier(AgentTier.DtmfOnly,      sp => new DtmfStrategyFactory(...))
//          .WithFallbackOrder(RealtimeVoice → SttTts → Nlu → DtmfOnly)
//          .AddObserver<SentimentObserver>()
//          .AddObserver<AcousticEmotionObserver>()
//          .AddObserver<PresenceObserver>()
//          .AddObserver<DashboardProjectionObserver>();
//
// AgentEnsembleStrategy is a strategy implementation, not a tier. It plugs in by
// registering its own factory at whichever tier the operator wants to use it for
// (typically RealtimeVoice).
