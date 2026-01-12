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
    public ContactCenterConversationSession Create(
        string sessionId,
        IServiceScope sessionScope,
        ILoggerFactory loggerFactory)
    {
        var hubSessionContext = new HubSessionContext(sessionId, sessionScope);
        return new ContactCenterConversationSession(sessionScope, hubSessionContext, loggerFactory);
    }

}
