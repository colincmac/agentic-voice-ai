using Agents.AI.RealtimeVoice.Azure.Calling.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

/// <summary>
/// Background service that bridges LiveCallRegistry events to the OperatorDashboardHub.
/// Automatically broadcasts call events to connected operator clients.
/// </summary>
public sealed class OperatorDashboardBroadcaster : IHostedService, IDisposable
{
    private readonly ILiveCallRegistry _liveCallRegistry;
    private readonly IHubContext<OperatorDashboardHub> _hubContext;
    private readonly ILogger<OperatorDashboardBroadcaster> _logger;

    public OperatorDashboardBroadcaster(
        ILiveCallRegistry liveCallRegistry,
        IHubContext<OperatorDashboardHub> hubContext,
        ILogger<OperatorDashboardBroadcaster>? logger = null)
    {
        _liveCallRegistry = liveCallRegistry;
        _hubContext = hubContext;
        _logger = logger ?? NullLogger<OperatorDashboardBroadcaster>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _liveCallRegistry.CallStarted += OnCallStarted;
        _liveCallRegistry.CallEnded += OnCallEnded;
        _liveCallRegistry.CallHealthUpdated += OnCallHealthUpdated;

        _logger.LogInformation("OperatorDashboardBroadcaster started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _liveCallRegistry.CallStarted -= OnCallStarted;
        _liveCallRegistry.CallEnded -= OnCallEnded;
        _liveCallRegistry.CallHealthUpdated -= OnCallHealthUpdated;

        _logger.LogInformation("OperatorDashboardBroadcaster stopped");
        return Task.CompletedTask;
    }

    private void OnCallStarted(object? sender, LiveCallSummary summary)
    {
        _ = BroadcastCallStartedAsync(summary);
    }

    private void OnCallEnded(object? sender, LiveCallSummary summary)
    {
        _ = BroadcastCallEndedAsync(summary);
    }

    private void OnCallHealthUpdated(object? sender, LiveCallSummary summary)
    {
        _ = BroadcastCallHealthUpdatedAsync(summary);
    }

    private async Task BroadcastCallStartedAsync(LiveCallSummary summary)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("CallStarted", summary);
            _logger.LogDebug("Broadcast CallStarted for session {SessionId}", summary.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast CallStarted for session {SessionId}", summary.SessionId);
        }
    }

    private async Task BroadcastCallEndedAsync(LiveCallSummary summary)
    {
        try
        {
            // Send both the full summary and a minimal "ended" notification
            await _hubContext.Clients.All.SendAsync("CallEnded", new { sessionId = summary.SessionId });

            // Also notify the session-specific group
            await _hubContext.Clients.Group($"session_{summary.SessionId}")
                .SendAsync("CallDetails", summary);

            _logger.LogDebug("Broadcast CallEnded for session {SessionId}", summary.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast CallEnded for session {SessionId}", summary.SessionId);
        }
    }

    private async Task BroadcastCallHealthUpdatedAsync(LiveCallSummary summary)
    {
        try
        {
            // Broadcast to all operators
            await _hubContext.Clients.All.SendAsync("CallHealthUpdated", summary);

            // Also broadcast to session-specific group for detailed views
            await _hubContext.Clients.Group($"session_{summary.SessionId}")
                .SendAsync("CallDetails", summary);

            _logger.LogDebug(
                "Broadcast CallHealthUpdated for session {SessionId}: Sentiment={Sentiment}, Risk={Risk}",
                summary.SessionId,
                summary.CustomerSentiment,
                summary.EscalationRiskScore);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast CallHealthUpdated for session {SessionId}", summary.SessionId);
        }
    }

    public void Dispose()
    {
        _liveCallRegistry.CallStarted -= OnCallStarted;
        _liveCallRegistry.CallEnded -= OnCallEnded;
        _liveCallRegistry.CallHealthUpdated -= OnCallHealthUpdated;
    }
}
