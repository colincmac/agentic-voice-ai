using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

/// <summary>
/// Extension methods for mapping the Operator Dashboard SignalR hub.
/// </summary>
public static class OperatorDashboardEndpointBuilderExtensions
{
    /// <summary>
    /// Maps the operator dashboard SignalR hub for real-time call monitoring.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">The path for the SignalR hub (default: "/operatorHub").</param>
    /// <returns>A hub endpoint convention builder.</returns>
    /// <remarks>
    /// <para>
    /// <b>TypeScript/JavaScript client example:</b>
    /// </para>
    /// <code>
    /// import { HubConnectionBuilder } from "@microsoft/signalr";
    ///
    /// const connection = new HubConnectionBuilder()
    ///   .withUrl("/operatorHub")
    ///   .withAutomaticReconnect()
    ///   .build();
    ///
    /// // Handle events
    /// connection.on("CallStarted", (summary: LiveCallSummary) => {
    ///   console.log("New call:", summary);
    /// });
    ///
    /// connection.on("CallEnded", (data: { sessionId: string }) => {
    ///   console.log("Call ended:", data.sessionId);
    /// });
    ///
    /// connection.on("CallHealthUpdated", (summary: LiveCallSummary) => {
    ///   console.log("Health update:", summary);
    /// });
    ///
    /// // Connect
    /// await connection.start();
    ///
    /// // Optionally subscribe to a specific session for detailed updates
    /// await connection.invoke("SubscribeToSession", "session_123");
    /// </code>
    /// </remarks>
    public static HubEndpointConventionBuilder MapOperatorDashboardHub(
        this IEndpointRouteBuilder endpoints,
        [StringSyntax("Route")] string path = "/operatorHub")
    {
        return endpoints.MapHub<OperatorDashboardHub>(path);
    }
}
