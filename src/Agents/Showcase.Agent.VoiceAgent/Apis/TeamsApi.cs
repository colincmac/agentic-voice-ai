using System.Diagnostics.CodeAnalysis;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;

namespace Showcase.Agent.VoiceAgent.Apis;

public static class TeamsApi
{

    public static void MapTeams(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string path = "teams")
    {
        var routeGroup = endpoints.MapGroup(path);

        var incomingRoute = routeGroup.MapPost("/messages", async (HttpRequest request, HttpResponse response, IAgentHttpAdapter adapter, IAgent agent, CancellationToken cancellationToken) =>
        {
            await adapter.ProcessAsync(request, response, agent, cancellationToken);
        });
    }
}
