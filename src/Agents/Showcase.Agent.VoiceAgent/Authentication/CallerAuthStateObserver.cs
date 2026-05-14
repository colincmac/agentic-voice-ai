using Agents.AI.RealtimeVoice.Azure.Calling.Proposed;
using Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Authentication;
using Microsoft.Extensions.Logging;

namespace Showcase.Agent.VoiceAgent.Authentication;

/// <summary>
/// <see cref="ICallObserver"/> that mirrors caller-authentication <see cref="StrategyEvent"/>s
/// into the <see cref="CallerAuthStateRegistry"/> so the diagnostics endpoint and operator
/// dashboard can report verification level without poking into the per-call DI scope.
/// </summary>
public sealed class CallerAuthStateObserver : ICallObserver
{
    private readonly CallerAuthStateRegistry _registry;
    private readonly ILogger<CallerAuthStateObserver> _logger;
    private Task? _pump;
    private CancellationTokenSource? _cts;
    private string? _callId;

    public CallerAuthStateObserver(CallerAuthStateRegistry registry, ILogger<CallerAuthStateObserver> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public string ObserverId => $"caller-auth-state-{Guid.NewGuid():N}";

    public Task StartAsync(CallObservation observation, CancellationToken cancellationToken = default)
    {
        _callId = observation.CallId;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pump = Task.Run(() => PumpAsync(observation, _cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is not null) { await _cts.CancelAsync().ConfigureAwait(false); }
        if (_pump is not null) { try { await _pump.ConfigureAwait(false); } catch { /* shutdown */ } }
        if (_callId is not null) { _registry.Remove(_callId); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }

    private async Task PumpAsync(CallObservation observation, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in observation.Events.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var record = _registry.GetOrAdd(observation.CallId);
                switch (evt)
                {
                    case StrategyEvent.CallerIdentified identified:
                        record.Identity = identified.Identity;
                        record.VerificationLevel = identified.Identity.VerificationLevel;
                        record.PendingChallenge = null;
                        record.Steps.Add(new AuthenticationStep(
                            identified.AuthenticatorName,
                            new AuthenticationOutcome.Authenticated(identified.Identity),
                            identified.At));
                        break;

                    case StrategyEvent.CallerAuthenticationFailed failed:
                        record.Steps.Add(new AuthenticationStep(
                            failed.AuthenticatorName,
                            new AuthenticationOutcome.Failed(failed.Reason),
                            failed.At));
                        break;

                    case StrategyEvent.CallerAuthenticationChallenge challenge:
                        record.PendingChallenge = challenge.Challenge;
                        break;

                    case StrategyEvent.CallerVerificationLevelChanged levelChanged:
                        record.VerificationLevel = levelChanged.To;
                        if (record.Identity.VerificationLevel < levelChanged.To)
                        {
                            record.Identity = record.Identity with { VerificationLevel = levelChanged.To };
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Caller-auth state observer faulted for call {CallId}", observation.CallId);
        }
    }
}
