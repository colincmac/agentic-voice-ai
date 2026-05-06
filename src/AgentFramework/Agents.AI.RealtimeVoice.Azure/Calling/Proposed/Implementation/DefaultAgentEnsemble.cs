using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Implementation;

/// <summary>
/// In-process implementation of <see cref="IAgentEnsemble"/>. Holds a fixed list of
/// speaker candidates plus a mutable list of delegates. Promotion is a metadata-only
/// swap — backend lifecycle is owned by <see cref="AgentEnsembleStrategy"/>.
/// </summary>
public sealed class DefaultAgentEnsemble : IAgentEnsemble
{
    private readonly List<IConversationalAgent> _candidates;
    private readonly ConcurrentDictionary<string, IDelegateAgent> _delegates;
    private readonly Channel<AgentInsight> _insights = Channel.CreateUnbounded<AgentInsight>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly Lock _swapLock = new();
    private string _activePrimaryId;
    private int _disposed;

    public DefaultAgentEnsemble(
        IEnumerable<IConversationalAgent> speakerCandidates,
        IEnumerable<IDelegateAgent>? delegates = null,
        string? initialPrimaryId = null)
    {
        _candidates = [.. speakerCandidates];
        if (_candidates.Count == 0)
        {
            throw new ArgumentException("At least one speaker candidate is required", nameof(speakerCandidates));
        }

        _delegates = new ConcurrentDictionary<string, IDelegateAgent>();
        foreach (var d in delegates ?? [])
        {
            _delegates[d.AgentId] = d;
        }

        _activePrimaryId = initialPrimaryId ?? _candidates[0].AgentId;
        if (_candidates.All(c => c.AgentId != _activePrimaryId))
        {
            throw new ArgumentException(
                $"initialPrimaryId '{_activePrimaryId}' is not among speaker candidates",
                nameof(initialPrimaryId));
        }
    }

    public IConversationalAgent PrimaryAgent
    {
        get
        {
            lock (_swapLock)
            {
                return _candidates.First(c => c.AgentId == _activePrimaryId);
            }
        }
    }

    public IReadOnlyList<IConversationalAgent> SpeakerCandidates => _candidates;

    public IReadOnlyList<IDelegateAgent> Delegates => [.. _delegates.Values];

    public ChannelReader<AgentInsight> Insights => _insights.Reader;

    public event Func<IConversationalAgent, ValueTask>? PrimaryChanged;

    public async ValueTask PromoteAsync(string speakerCandidateId, CancellationToken cancellationToken = default)
    {
        IConversationalAgent next;
        lock (_swapLock)
        {
            next = _candidates.FirstOrDefault(c => c.AgentId == speakerCandidateId)
                   ?? throw new ArgumentException(
                       $"Unknown speaker candidate '{speakerCandidateId}'",
                       nameof(speakerCandidateId));

            if (next.AgentId == _activePrimaryId)
            {
                return; // already primary
            }

            _activePrimaryId = next.AgentId;
        }

        var handlers = PrimaryChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Func<IConversationalAgent, ValueTask>>())
        {
            try { await handler(next).ConfigureAwait(false); }
            catch { /* observer failure must not block promotion */ }
        }
    }

    public ValueTask AddDelegateAsync(IDelegateAgent agent, CancellationToken cancellationToken = default)
    {
        _delegates[agent.AgentId] = agent;
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveDelegateAsync(string agentId, CancellationToken cancellationToken = default)
    {
        _delegates.TryRemove(agentId, out _);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Push an insight onto the ensemble's bus. Used by the strategy when delegates
    /// produce results, and by tests / external observers that want to inject context.
    /// </summary>
    public ValueTask PublishInsightAsync(AgentInsight insight, CancellationToken cancellationToken = default)
        => _insights.Writer.WriteAsync(insight, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _insights.Writer.TryComplete();

        foreach (var candidate in _candidates)
        {
            try { await candidate.Backend.DisposeAsync().ConfigureAwait(false); } catch { /* shutdown */ }
        }
    }
}
