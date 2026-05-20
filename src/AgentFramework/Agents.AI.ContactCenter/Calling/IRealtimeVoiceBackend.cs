using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.Calling;

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
    /// Replace the realtime backend's tool surface. Used by the strategy to push the
    /// guard-wrapped tool list for the current workflow step (typically obtained via
    /// <c>IIvrWorkflowNavigator.WrapToolsWithCurrentGuards</c>) at session start and on
    /// any subsequent step transition.
    /// </summary>
    /// <remarks>
    /// The default implementation is a no-op so backends that don't support live tool
    /// updates compile without changes. Production backends that drive a realtime model
    /// session must override and forward to the underlying realtime client.
    /// </remarks>
    ValueTask UpdateToolsAsync(IEnumerable<AITool> tools, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

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

    /// <summary>
    /// Emitted when the realtime model invokes a tool. Strategies use this to react to
    /// orchestration-level functions (for example the synthesized IVR <c>advance</c> tool
    /// that drives stage transitions) without having to subscribe to the underlying
    /// <c>FunctionCallContent</c> stream. <paramref name="Arguments"/> is the parsed
    /// argument dictionary; <paramref name="CallId"/> is the realtime provider's call id
    /// when available.
    /// </summary>
    public sealed record FunctionCalled(
        string Name,
        IReadOnlyDictionary<string, object?> Arguments,
        string? CallId,
        DateTimeOffset At) : RealtimeBackendUpdate(At);

    public sealed record Faulted(Exception Exception, string Message, DateTimeOffset At) : RealtimeBackendUpdate(At);
}
