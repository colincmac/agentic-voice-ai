using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Implementation;

/// <summary>
/// In-process implementation of <see cref="ICallQualityReporter"/>. Holds the live
/// snapshot per call, broadcasts each update to all subscribers.
/// </summary>
public sealed class InMemoryCallQualityReporter : ICallQualityReporter
{
    private readonly ConcurrentDictionary<string, CallQualitySnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<string, List<QualityAlert>> _alerts = new();
    private readonly List<Subscriber> _subscribers = [];
    private readonly Lock _subscribersLock = new();
    private readonly ILogger<InMemoryCallQualityReporter> _logger;

    public InMemoryCallQualityReporter(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<InMemoryCallQualityReporter>()
                  ?? NullLogger<InMemoryCallQualityReporter>.Instance;
    }

    /// <summary>
    /// Seed an initial snapshot when a call session starts. Required so updates
    /// have something to mutate.
    /// </summary>
    public void Register(CallQualitySnapshot initial)
    {
        _snapshots[initial.CallId] = initial;
        _alerts[initial.CallId] = [];
        Broadcast(initial);
    }

    public void Unregister(string callId)
    {
        _snapshots.TryRemove(callId, out _);
        _alerts.TryRemove(callId, out _);
    }

    public void Update(string callId, Func<CallQualitySnapshot, CallQualitySnapshot> mutate)
    {
        if (!_snapshots.TryGetValue(callId, out var current))
        {
            _logger.LogDebug("Update for unknown call {CallId}; ignoring", callId);
            return;
        }

        var next = mutate(current) with { UpdatedAt = DateTimeOffset.UtcNow };

        _snapshots[callId] = next;
        Broadcast(next);
    }

    public void RaiseAlert(string callId, QualityAlert alert)
    {
        if (!_alerts.TryGetValue(callId, out var list))
        {
            return;
        }

        lock (list)
        {
            list.Add(alert);
        }

        UpdateSnapshotAlerts(callId);
    }

    public void ResolveAlert(string callId, string alertId)
    {
        if (!_alerts.TryGetValue(callId, out var list))
        {
            return;
        }

        lock (list)
        {
            list.RemoveAll(a => a.AlertId == alertId);
        }

        UpdateSnapshotAlerts(callId);
    }

    public CallQualitySnapshot? TryGetSnapshot(string callId)
    {
        _snapshots.TryGetValue(callId, out var snap);
        return snap;
    }

    public IReadOnlyCollection<CallQualitySnapshot> GetActiveSnapshots()
        => _snapshots.Values.ToArray();

    public ChannelReader<CallQualitySnapshot> Subscribe(string? callIdFilter = null)
    {
        var channel = Channel.CreateUnbounded<CallQualitySnapshot>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        var subscriber = new Subscriber(channel.Writer, callIdFilter);
        lock (_subscribersLock)
        {
            _subscribers.Add(subscriber);
        }

        // Replay the current snapshot for the filter so new dashboards see "now" immediately.
        foreach (var snap in _snapshots.Values)
        {
            if (callIdFilter is null || snap.CallId == callIdFilter)
            {
                channel.Writer.TryWrite(snap);
            }
        }

        return channel.Reader;
    }

    private void UpdateSnapshotAlerts(string callId)
    {
        if (!_snapshots.TryGetValue(callId, out var current) || !_alerts.TryGetValue(callId, out var list))
        {
            return;
        }

        IReadOnlyList<QualityAlert> snapshot;
        lock (list)
        {
            snapshot = [.. list];
        }

        var next = current with { Alerts = snapshot, UpdatedAt = DateTimeOffset.UtcNow };
        _snapshots[callId] = next;
        Broadcast(next);
    }

    private void Broadcast(CallQualitySnapshot snapshot)
    {
        lock (_subscribersLock)
        {
            for (var i = _subscribers.Count - 1; i >= 0; i--)
            {
                var s = _subscribers[i];
                if (s.Filter is not null && s.Filter != snapshot.CallId)
                {
                    continue;
                }

                if (!s.Writer.TryWrite(snapshot))
                {
                    _subscribers.RemoveAt(i);
                }
            }
        }
    }

    private sealed record Subscriber(ChannelWriter<CallQualitySnapshot> Writer, string? Filter);
}
