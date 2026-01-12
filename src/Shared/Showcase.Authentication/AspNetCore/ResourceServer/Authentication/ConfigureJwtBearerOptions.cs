using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;


namespace Showcase.Authentication.AspNetCore.ResourceServer.Authentication;
public sealed class ConfigureJwtBearerOptions(ProtectedResourceJwtBearerEvents protectedResourceEvents) : IPostConfigureOptions<JwtBearerOptions>
{
    public string? Scheme { get; set; } = string.Empty;
    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(name);

        options.Events ??= new JwtBearerEvents();
        options.Events.OnChallenge = CreateChallengeCallback(options.Events.OnChallenge, protectedResourceEvents);

    }

    private Func<JwtBearerChallengeContext, Task> CreateChallengeCallback(Func<JwtBearerChallengeContext, Task> inner, ProtectedResourceJwtBearerEvents bearerEvents)
    {
        async Task Callback(JwtBearerChallengeContext ctx)
        {
            await inner(ctx);
            await bearerEvents.Challenge(ctx);
        }
        return Callback;
    }
}
