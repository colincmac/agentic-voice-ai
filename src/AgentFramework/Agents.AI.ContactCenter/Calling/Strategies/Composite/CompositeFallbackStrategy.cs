using System.Threading.Channels;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Calling.Strategies.Composite;

/// <summary>
/// Wraps an ordered list of strategy factories. Starts the first one; on
/// <see cref="StrategyEvent.Faulted"/> from the active inner, transparently
/// swaps to the next factory with the inner's <see cref="IvrWorkflowState"/>
/// preserved via <c>restoreFrom</c>. The caller's edge is never touched —
/// only the brain swaps.
/// </summary>
public sealed class CompositeFallbackStrategy : IConversationStrategy
{
    private readonly IReadOnlyList<IConversationStrategyFactory> _orderedFactories;
    private readonly RealtimeIvrWorkflowDefinition? _workflow;
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
    private int _factoryIndex = -1;

    public CompositeFallbackStrategy(
        IEnumerable<IConversationStrategyFactory> orderedFactories,
        RealtimeIvrWorkflowDefinition? workflow,
        ILoggerFactory? loggerFactory = null)
    {
        _orderedFactories = orderedFactories.ToArray();
        if (_orderedFactories.Count == 0)
        {
            throw new ArgumentException("At least one factory is required", nameof(orderedFactories));
        }
        _workflow = workflow;
        _logger = loggerFactory?.CreateLogger<CompositeFallbackStrategy>() ?? NullLogger<CompositeFallbackStrategy>.Instance;
    }

    public StrategyKind Kind => StrategyKind.Composite;

    public AgentTier Tier => _active?.Tier ?? _orderedFactories[Math.Max(0, _factoryIndex)].Tier;

    public IvrWorkflowState WorkflowState => _active?.WorkflowState ?? _placeholderState;
    private readonly IvrWorkflowState _placeholderState = new();

    public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;

    public EdgeCapabilities EmittedDirectives => _active?.EmittedDirectives ?? EdgeCapabilities.None;

    public ChannelReader<StrategyEvent> Events => _events.Reader;

    public Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default)
    {
        _startContext = context;
        return ActivateAsync(targetIndex: 0, restoreFrom: null, reason: "initial", cancellationToken);
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
        await StopAsync().ConfigureAwait(false);

        if (_active is not null)
        {
            try { await _active.DisposeAsync().ConfigureAwait(false); } catch { /* shutdown */ }
        }

        _cts.Dispose();
    }

    private async Task ActivateAsync(int targetIndex, IvrWorkflowState? restoreFrom, string reason, CancellationToken ct)
    {
        if (targetIndex >= _orderedFactories.Count)
        {
            _logger.LogError("No fallback available; composite exhausted");
            await _events.Writer.WriteAsync(
                new StrategyEvent.Faulted("No fallback available", null, DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
            _outbound.Writer.TryComplete();
            _events.Writer.TryComplete();
            return;
        }

        var factory = _orderedFactories[targetIndex];
        IConversationStrategy next;
        try
        {
            next = await factory.CreateAsync(
                _startContext!.CallId,
                _startContext.Services,
                _workflow,
                restoreFrom,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Strategy factory for tier {Tier} failed; trying next", factory.Tier);
            await ActivateAsync(targetIndex + 1, restoreFrom, $"factory-failed:{factory.Tier}", ct).ConfigureAwait(false);
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
            _factoryIndex = targetIndex;
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
            IConversationStrategy? current;
            int currentIndex;
            lock (_swapLock)
            {
                current = _active;
                currentIndex = _factoryIndex;
            }

            var stateToRestore = current?.WorkflowState;
            await ActivateAsync(currentIndex + 1, stateToRestore, fault.Message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Composite fallback handler crashed");
        }
    }
}
