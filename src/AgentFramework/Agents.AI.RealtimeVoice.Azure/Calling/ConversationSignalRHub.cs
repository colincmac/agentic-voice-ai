using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;
using Agents.AI.RealtimeVoice.Azure.Calling.Transports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

/// <summary>
/// SignalR Hub for real-time conversation messaging
/// Integrates with ContactCenterConversationHub for session management
/// </summary>
public sealed class ConversationSignalRHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();

        var conversationHub = httpContext?.RequestServices?.GetRequiredService<ContactCenterConversationHub>();
        if(conversationHub == null) throw new ArgumentNullException(nameof(conversationHub));

        var loggerFactory = httpContext?.RequestServices?.GetService<ILoggerFactory>();
        var hubLogger = loggerFactory?.CreateLogger<ContactCenterConversationHub>() ?? NullLogger<ContactCenterConversationHub>.Instance;
        // Expect query ?channelId= & sessionId= & participantId=
        var channelId = httpContext?.Request.Query["channelId"].ToString() ?? Guid.NewGuid().ToString("N");
        var sessionId = httpContext?.Request.Query["sessionId"].ToString() ?? "unknown-session";
        var participantId = httpContext?.Request.Query["participantId"].ToString() ?? channelId;
        var displayName = httpContext?.Request.Query["displayName"].ToString() ?? channelId;
        var participantType = Enum.TryParse(httpContext?.Request.Query["participantType"].ToString() ?? "Agent", out ParticipantType parsedType)
            ? parsedType
            : ParticipantType.Customer;

        hubLogger?.LogInformation(
            "SignalR connection {ConnectionId} for session {SessionId}, participant {ParticipantId}, channel {ChannelId}",
            Context.ConnectionId, sessionId, participantId, channelId);

        // Get or create session
        var session = conversationHub.GetOrCreateSession(sessionId);

        // Get or create participant
        var participantContext = session.GetOrAddParticipant(participantId, displayName);

        // Create SignalR transport
        var metadata = new ParticipantTransportMetadata
        {
            ContactId = channelId,
            ChannelType = CommunicationChannelType.ChatAIAgent,
            RawIdentifier = channelId,
            DisplayName = participantId,
            Role = ChannelRole.InteractiveMessaging | ChannelRole.ControlPlane,
            SupportsAudio = false,
            SupportsMessaging = true
        };

        var transport = new SignalRTransport(channelId, metadata, Clients.Caller);

        // Add transport to participant using the current HTTP request's service provider
        await session.AddTransportToParticipant(participantId, transport);

        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
        await base.OnConnectedAsync();

        hubLogger?.LogInformation(
            "SignalR transport {ChannelId} added to participant {ParticipantId} in session {SessionId}",
            channelId, participantId, sessionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var httpContext = Context.GetHttpContext();

        var conversationHub = httpContext?.RequestServices?.GetRequiredService<ContactCenterConversationHub>();
        if (conversationHub == null) throw new ArgumentNullException(nameof(conversationHub));
        var logger = httpContext?.RequestServices?.GetService<ILogger<ConversationSignalRHub>>();

        var channelId = Context.GetHttpContext()?.Request.Query["channelId"].ToString();
        var sessionId = Context.GetHttpContext()?.Request.Query["sessionId"].ToString();
        var participantId = Context.GetHttpContext()?.Request.Query["participantId"].ToString();

        logger?.LogInformation(
            "SignalR disconnection {ConnectionId} for session {SessionId}, participant {ParticipantId}. Exception: {Exception}",
            Context.ConnectionId, sessionId, participantId, exception?.Message);

        if (!string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(participantId) && !string.IsNullOrEmpty(channelId))
        {
            var session = conversationHub.TryGetSession(sessionId);
            if (session is not null)
            {
                await session.RemoveTransportFromParticipant(participantId, channelId);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task SendMessage([FromServices] ContactCenterConversationHub conversationHub, [FromServices] ILogger<ConversationSignalRHub> logger, string channelId, string sessionId, string role, string text)
    {
        var session = conversationHub.TryGetSession(sessionId);
        if (session is null)
        {
            logger.LogWarning("Session {SessionId} not found", sessionId);
            return;
        }

        var transport = session.GetParticipantContext(channelId);
        if (transport is null)
        {
            logger.LogWarning("Transport {ChannelId} not found in session {SessionId}", channelId, sessionId);
            return;
        }

        var msg = new MessageUpdate
        {
            CreatedAt = DateTimeOffset.UtcNow,
            SenderParticipantId = channelId,
            Role = role,
            Contents = [new TextContent(text)]
        };

        await transport.SendMessageAsync(msg);
    }
}


