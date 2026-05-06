namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed;

// SKETCH — adapter abstraction so RealtimeVoiceStrategy is decoupled from
// the concrete AuthorizingRealtimeAIAgent + RealtimeAIAgentSession plumbing.
//
// Why not just take IRealtimeAgent directly?
//   - RealtimeVoiceStrategy needs a single duplex stream of "agent updates"
//     (audio frames + transcripts + faults). That doesn't map cleanly onto
//     the existing IRealtimeAgent + AIAgent.RunStreamingAsync split.
//   - Tests would have to fake AIAgent + RealtimeAIAgentSession + an
//     entire AgentResponseUpdate stream just to exercise the strategy.
//
// The production adapter (AuthorizingAgentRealtimeBackend) wraps the existing
// agent stack. Tests use a tiny fake to drive faults on demand.

/// <summary>
/// A duplex realtime voice connection. The strategy writes caller audio in
/// and reads a stream of typed updates back out.
/// </summary>
public interface IRealtimeVoiceBackend : IAsyncDisposable
{
    string AgentId { get; }
    string AgentDisplayName { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Push a frame of caller audio into the realtime model.</summary>
    ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the system prompt mid-call (used when entering a new workflow step).
    /// Implementations may no-op if their backend doesn't support live prompt updates.
    /// </summary>
    ValueTask UpdateSystemPromptAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream of updates from the model. Completes when the connection closes.
    /// Implementations must surface faults as <see cref="RealtimeBackendUpdate.Faulted"/>
    /// rather than throwing inside the enumeration so the strategy can degrade gracefully.
    /// </summary>
    IAsyncEnumerable<RealtimeBackendUpdate> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Discriminated update from <see cref="IRealtimeVoiceBackend.RunAsync"/>.
/// Mirrors the StrategyEvent shape for easy translation.
/// </summary>
public abstract record RealtimeBackendUpdate(DateTimeOffset At)
{
    public sealed record Audio(ReadOnlyMemory<byte> Pcm, DateTimeOffset At) : RealtimeBackendUpdate(At);
    public sealed record Transcript(string Speaker, string Text, bool IsFinal, DateTimeOffset At) : RealtimeBackendUpdate(At);
    public sealed record AgentText(string Text, DateTimeOffset At) : RealtimeBackendUpdate(At);
    public sealed record Faulted(Exception Exception, string Message, DateTimeOffset At) : RealtimeBackendUpdate(At);
}
