using Agents.AI.RealtimeVoice.Azure.Models;
using Agents.AI.RealtimeVoice.Azure.Transports;
using Agents.Realtimevoice.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.Helpers;

public sealed class GrpcConversationStreamService : ConversationStream.ConversationStreamBase
{
    private readonly ILogger<GrpcConversationStreamService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;
    public GrpcConversationStreamService(IServiceProvider serviceProvider, ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _serviceProvider = serviceProvider;
        _logger = loggerFactory?.CreateLogger<GrpcConversationStreamService>() ?? NullLogger<GrpcConversationStreamService>.Instance;
    }

    public override async Task Connect(IAsyncStreamReader<ClientEnvelope> requestStream, IServerStreamWriter<ServerEnvelope> responseStream, ServerCallContext context)
    {
        // In a real implementation you would authenticate and map channelId.
        var channelId = context.RequestHeaders.GetValue("x-channel-id") ?? Guid.NewGuid().ToString("N");

        var metadata = new Models.ParticipantTransportMetadata
        {
            ContactId = channelId,
            ChannelType = CommunicationChannelType.Phone,
            RawIdentifier = channelId,
            SupportsAudio = true,
            SupportsMessaging = true
        };
        
        var transport = new GrpcTransport(channelId, metadata, _serviceProvider, responseStream, _loggerFactory.CreateLogger<GrpcTransport>());
        //_registry.Add(transport);

        transport.SetOnAudioReceivedCallback((cid, frame, ct) =>
        {
            // For now we just log; session routing will attach later.
            _logger.LogTrace("Inbound audio {Bytes} from {Channel}", frame.Length, cid);
            return Task.CompletedTask;
        });

        transport.SetOnMessageReceivedCallback((cid, msg, ct) =>
        {
            _logger.LogTrace("Inbound message from {Channel}", cid);
            return Task.CompletedTask;
        });

        try
        {
            await transport.HandleInboundAsync(requestStream, context.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            //_registry.Remove(channelId);
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
