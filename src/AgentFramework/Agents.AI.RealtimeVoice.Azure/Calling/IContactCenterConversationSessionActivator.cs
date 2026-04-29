using Agents.AI.RealtimeVoice.Azure.Calling.Routing;
using Agents.AI.RealtimeVoice.Azure.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

public interface IContactCenterConversationSessionActivator
{
    ContactCenterConversationSession Create(
        string sessionId,
        IServiceScope sessionScope,
        ILoggerFactory loggerFactory);
}

internal sealed class DefaultContactCenterConversationSessionActivator : IContactCenterConversationSessionActivator
{
    private readonly ISessionRouter _router;
    private readonly SessionTelemetry _telemetry;

    public DefaultContactCenterConversationSessionActivator(ISessionRouter router, SessionTelemetry telemetry)
    {
        _router = router;
        _telemetry = telemetry;
    }

    public ContactCenterConversationSession Create(
        string sessionId,
        IServiceScope sessionScope,
        ILoggerFactory loggerFactory)
    {
        var hubSessionContext = new HubSessionContext(sessionId, sessionScope);
        return new ContactCenterConversationSession(sessionScope, hubSessionContext, _router, _telemetry, loggerFactory);
    }
}
