using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

/// <summary>
/// SignalR hub for broadcasting operator dashboard events.
/// Operators connect to this hub to receive real-time updates about active calls.
/// </summary>
/// <remarks>
/// <para>
/// <b>Events broadcast by this hub:</b>
/// </para>
/// <list type="bullet">
///   <item><c>CallStarted(LiveCallSummary summary)</c> - When a new call begins</item>
///   <item><c>CallEnded({ sessionId: string })</c> - When a call ends</item>
///   <item><c>CallHealthUpdated(LiveCallSummary updated)</c> - When health metrics are updated</item>
/// </list>
/// <para>
/// <b>TypeScript/JavaScript client example:</b>
/// </para>
/// <code>
/// import { HubConnectionBuilder } from "@microsoft/signalr";
///
/// const connection = new HubConnectionBuilder()
///   .withUrl("/operatorHub")
///   .build();
///
/// connection.on("CallStarted", (summary) => {
///   console.log("New call started:", summary.sessionId);
/// });
///
/// connection.on("CallEnded", (data) => {
///   console.log("Call ended:", data.sessionId);
/// });
///
/// connection.on("CallHealthUpdated", (summary) => {
///   console.log("Health update:", summary.sessionId, summary.customerSentiment);
/// });
///
/// await connection.start();
/// </code>
/// </remarks>
public sealed class OperatorDashboardHub : Hub
{

    public override async Task OnConnectedAsync()
    {
        var logger = Context.GetHttpContext()?.RequestServices.GetService<ILogger<OperatorDashboardHub>>();
        var liveCallRegistry = Context.GetHttpContext()?.RequestServices.GetService<ILiveCallRegistry>();

        logger?.LogInformation("Operator client connected: {ConnectionId}", Context.ConnectionId);

        // Send current active calls to the newly connected operator
        var activeCalls = liveCallRegistry?.GetActiveCalls();
        if (activeCalls is null) throw new ArgumentNullException(nameof(activeCalls));

        foreach (var call in activeCalls)
        {
            await Clients.Caller.SendAsync("CallStarted", call);
        }

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Allows an operator to subscribe to updates for a specific session.
    /// </summary>
    /// <param name="sessionId">The session ID to subscribe to.</param>
    public async Task SubscribeToSession(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"session_{sessionId}");
    }

    /// <summary>
    /// Allows an operator to unsubscribe from updates for a specific session.
    /// </summary>
    /// <param name="sessionId">The session ID to unsubscribe from.</param>
    public async Task UnsubscribeFromSession(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"session_{sessionId}");
    }

    /// <summary>
    /// Allows an operator to request the current state of a specific call.
    /// </summary>
    /// <param name="sessionId">The session ID to get details for.</param>
    public async Task GetCallDetails(string sessionId, [FromServices] ILiveCallRegistry liveCallRegistry)
    {
        var call = liveCallRegistry.GetBySessionId(sessionId);

        if (call is not null)
        {
            await Clients.Caller.SendAsync("CallDetails", call);
        }
        else
        {
            await Clients.Caller.SendAsync("CallNotFound", new { sessionId });
        }
    }
}
internal class ConnectionList : IReadOnlyCollection<ConnectionContext>
{
    private readonly ConcurrentDictionary<string, ConnectionContext> _connections = new ConcurrentDictionary<string, ConnectionContext>(StringComparer.Ordinal);

    public ConnectionContext? this[string connectionId]
    {
        get
        {
            if (_connections.TryGetValue(connectionId, out var connection))
            {
                return connection;
            }
            return null;
        }
    }

    public int Count => _connections.Count;

    public void Add(ConnectionContext connection)
    {
        _connections.TryAdd(connection.ConnectionId, connection);
    }

    public void Remove(ConnectionContext connection)
    {
        _connections.TryRemove(connection.ConnectionId, out var dummy);
    }

    public IEnumerator<ConnectionContext> GetEnumerator()
    {
        foreach (var item in _connections)
        {
            yield return item.Value;
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
public class ContactCenterConnectionHandler : ConnectionHandler
{
    private ConnectionList Connections { get; } = new ConnectionList();

    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        Connections.Add(connection);

        var transportType = connection.Features.Get<IHttpTransportFeature>()?.TransportType;

        await Broadcast($"{connection.ConnectionId} connected ({transportType})");

        try
        {
            while (true)
            {
                var result = await connection.Transport.Input.ReadAsync();
                var buffer = result.Buffer;

                try
                {
                    if (!buffer.IsEmpty)
                    {
                        // We can avoid the copy here but we'll deal with that later
                        var text = Encoding.UTF8.GetString(buffer.ToArray());
                        text = $"{connection.ConnectionId}: {text}";
                        await Broadcast(Encoding.UTF8.GetBytes(text));
                    }
                    else if (result.IsCompleted)
                    {
                        break;
                    }
                }
                finally
                {
                    connection.Transport.Input.AdvanceTo(buffer.End);
                }
            }
        }
        finally
        {
            Connections.Remove(connection);

            await Broadcast($"{connection.ConnectionId} disconnected ({transportType})");
        }
    }

    private Task Broadcast(string text)
    {
        return Broadcast(Encoding.UTF8.GetBytes(text));
    }

    private Task Broadcast(byte[] payload)
    {
        var tasks = new List<Task>(Connections.Count);
        foreach (var c in Connections)
        {
            tasks.Add(c.Transport.Output.WriteAsync(payload).AsTask());
        }

        return Task.WhenAll(tasks);
    }
}
