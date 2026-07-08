using System.Threading.Channels;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Calling.Strategies.Composite;

/// <summary>
/// Wraps an ordered list of <see cref="AgentTier"/> values. Starts the first one by
/// resolving an <see cref="IConversationStrategy"/> keyed by that tier from the per-call
/// service scope (<see cref="StrategyStartContext.Services"/>); on a
/// <see cref="StrategyEvent.Faulted"/> from the active inner, transparently resolves the
/// next tier from the same scope. Per-call <see cref="IvrWorkflowState"/> is preserved
/// across tier swaps because every inner strategy in the chain reads it from the scoped
/// registration in the call scope. The caller's edge is never touched — only the brain swaps.
/// </summary>
public sealed class CompositeFallbackStrategy : IConversationStrategy
{
    private readonly IReadOnlyList<AgentTier> _orderedTiers;
    private readonly ILogger _logger;

    private readonly Channel<OutboundDirective> _outbound = Channel.CreateBounded<OutboundDirective>(
        new BoundedChannelOptions(500)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly Channel<StrategyEvent> _events = Channel.CreateUnbounded<StrategyEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _swapLock = new();

    private StrategyStartContext? _startContext;
    private IConversationStrategy? _active;
    private CancellationTokenSource? _activePumpCts;
    private Task? _activePumps;
    private int _tierIndex = -1;
    private int _disposed;

    public CompositeFallbackStrategy(
        IEnumerable<AgentTier> orderedTiers,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(orderedTiers);
        _orderedTiers = orderedTiers.ToArray();
        if (_orderedTiers.Count == 0)
        {
            throw new ArgumentException("At least one tier is required", nameof(orderedTiers));
        }
        _logger = loggerFactory?.CreateLogger<CompositeFallbackStrategy>() ?? NullLogger<CompositeFallbackStrategy>.Instance;
    }

    public StrategyKind Kind => StrategyKind.Composite;

    public AgentTier Tier => _active?.Tier ?? _orderedTiers[Math.Max(0, _tierIndex)];

    public IvrWorkflowState WorkflowState => _active?.WorkflowState ?? _placeholderState;
    private readonly IvrWorkflowState _placeholderState = new();

    public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;

    public EdgeCapabilities EmittedDirectives => _active?.EmittedDirectives ?? EdgeCapabilities.None;

    public ChannelReader<StrategyEvent> Events => _events.Reader;

    public Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _startContext = context;
        return ActivateAsync(targetIndex: 0, reason: "initial", cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        IConversationStrategy? toStop;
        Task? pumps;
        lock (_swapLock)
        {
            toStop = _active;
            pumps = _activePumps;
            _active = null;
            _activePumps = null;
        }

        if (toStop is not null)
        {
            try { await toStop.StopAsync(cancellationToken).ConfigureAwait(false); } catch { /* shutdown */ }
        }

        if (pumps is not null)
        {
            try { await pumps.ConfigureAwait(false); } catch { /* shutdown */ }
        }

        _outbound.Writer.TryComplete();
        _events.Writer.TryComplete();
    }

    public ValueTask SuspendAsync(CancellationToken cancellationToken = default)
        => _active?.SuspendAsync(cancellationToken) ?? ValueTask.CompletedTask;

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default)
        => _active?.ResumeAsync(cancellationToken) ?? ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);

        if (_active is not null)
        {
            try { await _active.DisposeAsync().ConfigureAwait(false); } catch { /* shutdown */ }
        }

        _cts.Dispose();
    }

    private async Task ActivateAsync(int targetIndex, string reason, CancellationToken ct)
    {
        if (targetIndex >= _orderedTiers.Count)
        {
            _logger.LogError("No fallback available; composite exhausted");
            await _events.Writer.WriteAsync(
                new StrategyEvent.Faulted("No fallback available", null, DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
            _outbound.Writer.TryComplete();
            _events.Writer.TryComplete();
            return;
        }

        var tier = _orderedTiers[targetIndex];
        IConversationStrategy next;
        try
        {
            // Resolve the inner strategy from the per-call scope via keyed DI. Each resolve
            // produces a fresh instance (registered as keyed transient) because composite
            // swaps must build a new strategy with a fresh backend session each time.
            next = _startContext!.Services.GetRequiredKeyedService<IConversationStrategy>(tier);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No IConversationStrategy registered for tier {Tier}; trying next", tier);
            await ActivateAsync(targetIndex + 1, $"resolve-failed:{tier}", ct).ConfigureAwait(false);
            return;
        }

        IConversationStrategy? previous;
        Task? previousPumps;
        AgentTier? previousTier;

        lock (_swapLock)
        {
            previous = _active;
            previousPumps = _activePumps;
            previousTier = previous?.Tier;
            _active = next;
            _tierIndex = targetIndex;
            _activePumpCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _activePumps = Task.WhenAll(
                PumpAudioAsync(next, _activePumpCts.Token),
                PumpEventsAsync(next, _activePumpCts.Token));
        }

        // Stop the previous AFTER the swap so its faulted event doesn't race a new one.
        if (previous is not null)
        {
            try { await previous.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* tolerated */ }
            if (previousPumps is not null)
            {
                try { await previousPumps.ConfigureAwait(false); } catch { /* tolerated */ }
            }
            try { await previous.DisposeAsync().ConfigureAwait(false); } catch { /* tolerated */ }
        }

        await next.StartAsync(_startContext!, ct).ConfigureAwait(false);

        if (previousTier is { } from)
        {
            await _events.Writer.WriteAsync(
                new StrategyEvent.TierDegraded(from, next.Tier, reason, DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task PumpAudioAsync(IConversationStrategy inner, CancellationToken ct)
    {
        try
        {
            await foreach (var directive in inner.Outbound.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await _outbound.Writer.WriteAsync(directive, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* swap or shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Composite audio pump terminated for inner {Tier}", inner.Tier);
        }
    }

    private async Task PumpEventsAsync(IConversationStrategy inner, CancellationToken ct)
    {
        try
        {
            await foreach (var ev in inner.Events.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (ev is StrategyEvent.Faulted fault)
                {
                    // Don't surface the inner Faulted to observers as a call-killing fault —
                    // we'll emit TierDegraded after the swap completes.
                    _logger.LogInformation("Inner strategy {Tier} reported Faulted: {Message}; degrading",
                        inner.Tier, fault.Message);
                    _ = Task.Run(() => HandleInnerFaultAsync(fault, ct), CancellationToken.None);
                    return;
                }
                await _events.Writer.WriteAsync(ev, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* swap or shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Composite event pump terminated for inner {Tier}", inner.Tier);
        }
    }

    private async Task HandleInnerFaultAsync(StrategyEvent.Faulted fault, CancellationToken ct)
    {
        try
        {
            int currentIndex;
            lock (_swapLock)
            {
                currentIndex = _tierIndex;
            }

            // No restoreFrom plumbing needed: IvrWorkflowState is registered as a scoped
            // service in the call scope, so the next inner reads the same instance the
            // previous inner mutated.
            await ActivateAsync(currentIndex + 1, fault.Message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Composite fallback handler crashed");
        }
    }
}
