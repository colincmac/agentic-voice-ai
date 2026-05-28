using System.Diagnostics.CodeAnalysis;
using Agents.AI.ContactCenter.Calling;
using Microsoft.AspNetCore.Mvc;

namespace Showcase.Agent.VoiceAgent.Apis;

/// <summary>
/// API endpoints for the operator dashboard, ported onto the new
/// <see cref="ICallSessionRegistry"/> + <see cref="ICallQualityReporter"/> shape.
/// Replaces the legacy <c>ILiveCallRegistry</c> projection.
/// </summary>
public static class OperatorCallsApi
{
    public static void MapOperatorCalls(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string path = "api/operator/calls")
    {
        var routeGroup = endpoints.MapGroup(path).WithTags("Operator Dashboard");

        // GET /api/operator/calls/active — every active call's current quality snapshot.
        routeGroup.MapGet("/active", (
            [FromServices] ICallSessionRegistry registry,
            [FromServices] ICallQualityReporter quality) =>
        {
            var snapshots = quality.GetActiveSnapshots();
            var sessionsById = registry.ActiveSessions.ToDictionary(s => s.CallId);

            // Project to the dashboard view: snapshot + transient session-only info
            // (supervisor edge id, observers wired) that the snapshot doesn't carry.
            var view = snapshots
                .Select(snap => new ActiveCallView
                {
                    Snapshot = snap,
                    SupervisorEdgeId = sessionsById.TryGetValue(snap.CallId, out var session)
                        ? session.SupervisorEdge?.EdgeId
                        : null
                })
                .ToArray();

            return Results.Ok(view);
        })
        .WithName("GetActiveCalls")
        .WithDescription("Returns every active call's current quality snapshot for the operator dashboard.")
        .Produces<IReadOnlyCollection<ActiveCallView>>(StatusCodes.Status200OK);

        // GET /api/operator/calls/{callId} — one call's snapshot.
        routeGroup.MapGet("/{callId}", (
            [FromRoute] string callId,
            [FromServices] ICallSessionRegistry registry,
            [FromServices] ICallQualityReporter quality) =>
        {
            var snap = quality.TryGetSnapshot(callId);
            if (snap is null)
            {
                return Results.NotFound(new { message = $"Call '{callId}' not found." });
            }

            var session = registry.TryGet(callId);
            return Results.Ok(new ActiveCallView
            {
                Snapshot = snap,
                SupervisorEdgeId = session?.SupervisorEdge?.EdgeId
            });
        })
        .WithName("GetCallById")
        .WithDescription("Returns the current quality snapshot for a specific call.")
        .Produces<ActiveCallView>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);
    }
}

/// <summary>
/// Dashboard projection: combines a quality snapshot with session-only context
/// (active supervisor edge) that the snapshot doesn't track directly.
/// </summary>
public sealed record ActiveCallView
{
    public required CallQualitySnapshot Snapshot { get; init; }
    public string? SupervisorEdgeId { get; init; }
}
