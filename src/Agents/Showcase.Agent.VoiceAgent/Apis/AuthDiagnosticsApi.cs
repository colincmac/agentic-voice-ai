using System.Diagnostics.CodeAnalysis;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Authentication;
using Showcase.Agent.VoiceAgent.Authentication;

namespace Showcase.Agent.VoiceAgent.Apis;

/// <summary>
/// Diagnostics endpoints that expose caller-authentication state for the showcase demo.
/// State is mirrored from <see cref="StrategyEvent"/>s by <see cref="CallerAuthStateObserver"/>
/// into the singleton <see cref="CallerAuthStateRegistry"/>, so reads are lock-free and don't
/// touch the per-call DI scope.
/// </summary>
public static class AuthDiagnosticsApi
{
    public static void MapAuthDiagnostics(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string path = "api/diagnostics/auth")
    {
        var routeGroup = endpoints.MapGroup(path).WithTags("Auth Diagnostics").AllowAnonymous();

        routeGroup.MapGet("/", (
            ICallSessionRegistry registry,
            CallerAuthStateRegistry authRegistry) =>
        {
            var sessionsById = registry.ActiveSessions.ToDictionary(s => s.CallId);
            var view = authRegistry.Snapshot()
                .Select(kv =>
                {
                    sessionsById.TryGetValue(kv.Key, out var session);
                    return new AuthDiagnosticsView
                    {
                        CallId = kv.Key,
                        StrategyKind = session?.Strategy.Kind.ToString() ?? "unknown",
                        Tier = session?.Strategy.Tier.ToString() ?? "unknown",
                        State = session?.State.ToString() ?? "unknown",
                        StartedAt = session?.StartedAt ?? DateTimeOffset.MinValue,
                        Identity = kv.Value.Identity,
                        VerificationLevel = kv.Value.VerificationLevel.ToString(),
                        PendingChallenge = kv.Value.PendingChallenge,
                        Steps = kv.Value.Steps
                    };
                })
                .ToArray();

            return Results.Ok(view);
        })
        .WithName("GetCallerAuthDiagnostics")
        .WithDescription("List active calls with their caller-authentication state.");

        routeGroup.MapGet("/{callId}", (string callId, CallerAuthStateRegistry authRegistry) =>
        {
            var record = authRegistry.TryGet(callId);
            return record is null
                ? Results.NotFound(new { message = $"No auth state for call '{callId}'." })
                : Results.Ok(new AuthDiagnosticsView
                {
                    CallId = callId,
                    StrategyKind = "n/a",
                    Tier = "n/a",
                    State = "n/a",
                    StartedAt = DateTimeOffset.MinValue,
                    Identity = record.Identity,
                    VerificationLevel = record.VerificationLevel.ToString(),
                    PendingChallenge = record.PendingChallenge,
                    Steps = record.Steps
                });
        })
        .WithName("GetCallerAuthDiagnosticsById");
    }
}

/// <summary>Diagnostics projection for a single active call.</summary>
public sealed record AuthDiagnosticsView
{
    public required string CallId { get; init; }
    public required string StrategyKind { get; init; }
    public required string Tier { get; init; }
    public required string State { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public CallerIdentity? Identity { get; init; }
    public required string VerificationLevel { get; init; }
    public AuthenticationChallenge? PendingChallenge { get; init; }
    public required IReadOnlyList<AuthenticationStep> Steps { get; init; }
}
