using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

/// <summary>
/// Categorizes context events flowing through the session.
/// </summary>
public enum HubSessionEventKind
{
    /// <summary>A voice transcript (user or agent utterance).</summary>
    Transcript,

    /// <summary>A chat message from any interactive channel.</summary>
    ChatMessage,

    /// <summary>A request for approval routed to a supervisor or control plane.</summary>
    ApprovalRequest,

    /// <summary>An approval or denial decision from a supervisor.</summary>
    ApprovalDecision,

    /// <summary>An insight produced by an agent transport (A2A agent, background agent, etc.).</summary>
    AgentInsight,

    /// <summary>Structured data from a system integration (CRM record, ticket update).</summary>
    SystemData,

    /// <summary>A participant joined the session.</summary>
    ParticipantJoined,

    /// <summary>A participant left the session.</summary>
    ParticipantLeft,

    /// <summary>A transfer has been initiated for this session.</summary>
    TransferInitiated,

    /// <summary>A transfer has completed for this session.</summary>
    TransferCompleted,

    /// <summary>A presence/silence timeout occurred — no person activity detected within the configured interval.</summary>
    PresenceTimeout,

    /// <summary>Application-defined event kind.</summary>
    Unknown
}

/// <summary>
/// A typed context event published to the session bus.
/// Channels and agents produce these; subscribers consume them to build
/// a unified understanding across all interaction modalities.
/// </summary>
public sealed record SessionContextEvent
{
    public required string EventId { get; init; }
    public required HubSessionEventKind Kind { get; init; }
    public required string SourceParticipantId { get; init; }
    public string? SourceChannelId { get; init; }

    /// <summary>
    /// When set, only subscribers matching this participant receive the event.
    /// When null, the event is broadcast to all subscribers whose filter matches.
    /// </summary>
    public string? TargetParticipantId { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Structured payload. Use pattern matching to process.
    /// Examples: transcript text, approval result, vision analysis, CRM record.
    /// </summary>
    public required object Payload { get; init; }

    /// <summary>
    /// Optional tags for filtering (e.g., "modality:screen", "approval:transfer").
    /// </summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Session-scoped typed pub/sub for structured context events.
/// All channels publish context here; the primary AI agent and other
/// participants subscribe to build awareness across all modalities.
/// <para>
/// This bus is strictly for <em>context</em> (transcripts, chat, CRM, approvals, agent insights).
/// Real-time audio frames never flow through this bus — they stay on their
/// dedicated <see cref="Extensions.Helpers.Streaming.RawMediaStreamChannel"/> path
/// to guarantee zero added latency.
/// </para>
/// </summary>
public sealed class HubSessionEventBus : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, SessionContextSubscription> _subscriptions = new();
    private readonly ConcurrentQueue<SessionContextEvent> _eventLog = new();
    private int _disposed;

    /// <summary>
    /// Maximum number of events retained in the history ring buffer.
    /// </summary>
    public int MaxHistorySize { get; init; } = 1_000;

    /// <summary>
    /// Gets a read-only snapshot of recent events (for late-joining agents).
    /// </summary>
    public IReadOnlyList<SessionContextEvent> EventHistory => [.. _eventLog];

    /// <summary>
    /// Publishes a context event to all matching subscribers.
    /// This completes synchronously when subscriber channels use <see cref="BoundedChannelFullMode.DropOldest"/>
    /// (the default), ensuring no blocking on the caller's hot path.
    /// </summary>
    public ValueTask PublishAsync(SessionContextEvent contextEvent, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        EnqueueHistory(contextEvent);

        foreach (var sub in _subscriptions.Values)
        {
            if (sub.Filter is not null && !sub.Filter(contextEvent))
            {
                continue;
            }

            // TryWrite is non-blocking; DropOldest ensures we never stall the publisher.
            sub.Channel.Writer.TryWrite(contextEvent);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Creates a filtered subscription. Only events matching the predicate are delivered.
    /// Pass null to receive all events.
    /// </summary>
    public SessionContextSubscription Subscribe(Func<SessionContextEvent, bool>? filter = null)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var sub = new SessionContextSubscription(this, filter);
        _subscriptions.TryAdd(sub.Id, sub);

        return sub;
    }

    internal void Unsubscribe(Guid id) => _subscriptions.TryRemove(id, out _);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var sub in _subscriptions.Values)
        {
            await sub.DisposeAsync();
        }
        _subscriptions.Clear();
    }

    private void EnqueueHistory(SessionContextEvent contextEvent)
    {
        _eventLog.Enqueue(contextEvent);

        // Trim to max size (approximate — ConcurrentQueue.Count is O(1) but not perfectly synchronized)
        while (_eventLog.Count > MaxHistorySize)
        {
            _eventLog.TryDequeue(out _);
        }
    }
}

/// <summary>
/// A single subscriber to the <see cref="HubSessionEventBus"/> with optional filtering.
/// </summary>
public sealed class SessionContextSubscription : IAsyncDisposable
{
    private readonly HubSessionEventBus _bus;
    private int _disposed;

    internal SessionContextSubscription(HubSessionEventBus bus, Func<SessionContextEvent, bool>? filter)
    {
        _bus = bus;
        Filter = filter;
        Channel = System.Threading.Channels.Channel.CreateBounded<SessionContextEvent>(
            new BoundedChannelOptions(500)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
    }

    public Guid Id { get; } = Guid.NewGuid();
    internal Func<SessionContextEvent, bool>? Filter { get; }
    internal Channel<SessionContextEvent> Channel { get; }

    /// <summary>
    /// Number of buffered events ready to read.
    /// </summary>
    public int Available => Channel.Reader.Count;

    /// <summary>
    /// Reads context events as an async stream.
    /// </summary>
    public async IAsyncEnumerable<SessionContextEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in Channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        Channel.Writer.TryComplete();
        _bus.Unsubscribe(Id);

        return ValueTask.CompletedTask;
    }
}
