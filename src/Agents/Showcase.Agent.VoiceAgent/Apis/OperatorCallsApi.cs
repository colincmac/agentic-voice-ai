using System.Diagnostics.CodeAnalysis;
using Agents.AI.RealtimeVoice.Azure.Calling;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;
using Microsoft.AspNetCore.Mvc;

namespace Showcase.Agent.VoiceAgent.Apis;

/// <summary>
/// API endpoints for the operator dashboard to monitor live calls.
/// </summary>
public static class OperatorCallsApi
{
    /// <summary>
    /// Maps the operator calls API endpoints.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">The base path for the operator API.</param>
    public static void MapOperatorCalls(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string path = "api/operator/calls")
    {
        var routeGroup = endpoints.MapGroup(path)
            .WithTags("Operator Dashboard");

        // GET /api/operator/calls/active - Get all active calls
        routeGroup.MapGet("/active", (
            [FromServices] ILiveCallRegistry liveCallRegistry) =>
        {
            var activeCalls = liveCallRegistry.GetActiveCalls();
            return Results.Ok(activeCalls);
        })
        .WithName("GetActiveCalls")
        .WithDescription("Returns all currently active calls for the operator dashboard.")
        .Produces<IReadOnlyCollection<LiveCallSummary>>(StatusCodes.Status200OK);

        // GET /api/operator/calls/{sessionId} - Get a specific call by session ID
        routeGroup.MapGet("/{sessionId}", (
            [FromRoute] string sessionId,
            [FromServices] ILiveCallRegistry liveCallRegistry) =>
        {
            var call = liveCallRegistry.GetBySessionId(sessionId);

            if (call is null)
            {
                return Results.NotFound(new { message = $"Call with session ID '{sessionId}' not found." });
            }

            return Results.Ok(call);
        })
        .WithName("GetCallBySessionId")
        .WithDescription("Returns details for a specific call by its session ID.")
        .Produces<LiveCallSummary>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);
    }
}
