using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.RealtimeVoice.Azure.Calling;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;
using Agents.AI.RealtimeVoice.Azure.Calling.Transports;
using Agents.AI.RealtimeVoice.Azure.Tests.Mocks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.RealtimeVoice.Azure.Tests;

/// <summary>
/// Tests for thread safety and concurrency issues in the transport layer
/// </summary>
public class ThreadSafetyTests
{
    [Fact]
    public async Task HubSessionParticipantContext_SendAudioAsync_AwaitsAllTransports()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IServiceProvider>(sp => sp);
        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var participant = new HubSessionParticipantContext(scope, "test-participant");

        var transport1 = new MockChannelTransport("transport-1");
        var transport2 = new MockChannelTransport("transport-2");

        await participant.AddTransportAsync(transport1);
        await participant.AddTransportAsync(transport2);

        var audioData = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        await participant.SendAudioAsync(audioData);

        // Assert - both transports should have received the audio
        Assert.Equal(1, transport1.AudioCallCount);
        Assert.Equal(1, transport2.AudioCallCount);
    }

    [Fact]
    public async Task HubSessionParticipantContext_SendMessageAsync_AwaitsAllTransports()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IServiceProvider>(sp => sp);
        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var participant = new HubSessionParticipantContext(scope, "test-participant");

        var transport1 = new MockChannelTransport("transport-1");
        var transport2 = new MockChannelTransport("transport-2");

        await participant.AddTransportAsync(transport1);
        await participant.AddTransportAsync(transport2);

        var message = new MessageUpdate
        {
            CreatedAt = DateTimeOffset.UtcNow,
            SenderParticipantId = "sender",
            Role = "user",
            Contents = [new TextContent("Test message")]
        };

        // Act
        await participant.SendMessageAsync(message);

        // Assert - both transports should have received the message
        Assert.Equal(1, transport1.MessageCallCount);
        Assert.Equal(1, transport2.MessageCallCount);
    }

    [Fact]
    public async Task HubSessionParticipantContext_ConcurrentAccess_ThreadSafe()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IServiceProvider>(sp => sp);
        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var participant = new HubSessionParticipantContext(scope, "test-participant");

        // Act - add multiple transports concurrently
        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            var transport = new MockChannelTransport($"transport-{i}");
            await participant.AddTransportAsync(transport);
        });

        await Task.WhenAll(tasks);

        // Assert - all transports should be added
        Assert.Equal(10, participant.Transports.Count);
    }

    [Fact]
    public async Task HubSessionParticipantContext_Metadata_AggregatesAllTransportCapabilities()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IServiceProvider>(sp => sp);
        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var participant = new HubSessionParticipantContext(scope, "test-participant");

        var audioOnlyMetadata = new ParticipantTransportMetadata
        {
            ContactId = "test-1",
            ChannelType = CommunicationChannelType.Phone,
            RawIdentifier = "test-1",
            SupportsAudio = true,
            SupportsMessaging = false,
            SupportsVideo = false
        };

        var messagingOnlyMetadata = new ParticipantTransportMetadata
        {
            ContactId = "test-2",
            ChannelType = CommunicationChannelType.ChatAIAgent,
            RawIdentifier = "test-2",
            SupportsAudio = false,
            SupportsMessaging = true,
            SupportsVideo = false
        };

        var transport1 = new MockChannelTransport("transport-1", audioOnlyMetadata);
        var transport2 = new MockChannelTransport("transport-2", messagingOnlyMetadata);

        await participant.AddTransportAsync(transport1);
        await participant.AddTransportAsync(transport2);

        // Act
        var metadata = participant.Metadata;

        // Assert - should aggregate capabilities from all transports
        Assert.True(metadata.SupportsAudio);
        Assert.True(metadata.SupportsMessaging);
        Assert.False(metadata.SupportsVideo);
    }

    [Fact]
    public async Task ScopedChannelTransport_DoubleDispose_DoesNotCallOnDisposedTwice()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IServiceProvider>(sp => sp);
        var provider = services.BuildServiceProvider();

        var innerTransport = new MockChannelTransport("test-transport");
        int disposedCallCount = 0;

        var scopedTransport = new ScopedChannelTransport(
            innerTransport,
            provider,
            async _ => { disposedCallCount++; await Task.CompletedTask; });

        // Act - dispose twice
        await scopedTransport.DisposeAsync();
        await scopedTransport.DisposeAsync();

        // Assert - onDisposed should only be called once
        Assert.Equal(1, disposedCallCount);
    }

    [Fact]
    public async Task HubSessionParticipantContext_DoubleDispose_DoesNotCallOnDisconnectedTwice()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IServiceProvider>(sp => sp);
        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var participant = new HubSessionParticipantContext(scope, "test-participant");

        int disconnectedCallCount = 0;
        participant.OnDisconnected(async _ => { disconnectedCallCount++; await Task.CompletedTask; });

        // Act - dispose twice
        await participant.DisposeAsync();
        await participant.DisposeAsync();

        // Assert - OnDisconnected should only be called once
        Assert.Equal(1, disconnectedCallCount);
    }

    [Fact]
    public async Task ContactCenterConversationSession_SendsAudio_AwaitsAllParticipants()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IServiceProvider>(sp => sp);
        var provider = services.BuildServiceProvider();
        using var sessionScope = provider.CreateScope();

        var hubContext = new HubSessionContext("test-session");
        var session = new ContactCenterConversationSession(sessionScope, hubContext);

        // Add two participants with transports
        var participant1 = session.GetOrAddParticipant("participant-1");
        var participant2 = session.GetOrAddParticipant("participant-2");

        var transport1 = new MockChannelTransport("transport-1");
        var transport2 = new MockChannelTransport("transport-2");

        await participant1.AddTransportAsync(transport1);
        await participant2.AddTransportAsync(transport2);

        var audioData = new byte[] { 1, 2, 3, 4, 5 };

        // Act - simulate audio from participant 1
        await transport1.SimulateInboundAudioAsync(audioData);

        // Give a small delay for async processing
        await Task.Delay(100);

        // Assert - participant 2 should have received the audio
        Assert.Equal(1, transport2.AudioCallCount);
        Assert.True(transport2.ReceivedAudio[0].Span.SequenceEqual(audioData));
    }

    [Fact]
    public async Task ContactCenterConversationSession_SendsMessage_AwaitsAllParticipants()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IServiceProvider>(sp => sp);
        var provider = services.BuildServiceProvider();
        using var sessionScope = provider.CreateScope();

        var hubContext = new HubSessionContext("test-session");
        var session = new ContactCenterConversationSession(sessionScope, hubContext);

        // Add two participants with transports
        var participant1 = session.GetOrAddParticipant("participant-1");
        var participant2 = session.GetOrAddParticipant("participant-2");

        var transport1 = new MockChannelTransport("transport-1");
        var transport2 = new MockChannelTransport("transport-2");

        await participant1.AddTransportAsync(transport1);
        await participant2.AddTransportAsync(transport2);

        var message = new MessageUpdate
        {
            CreatedAt = DateTimeOffset.UtcNow,
            SenderParticipantId = "participant-1",
            Role = "user",
            Contents = [new TextContent("Hello")]
        };

        // Act - simulate message from participant 1
        await transport1.SimulateInboundMessageAsync(message);

        // Give a small delay for async processing
        await Task.Delay(100);

        // Assert - participant 2 should have received the message
        Assert.Equal(1, transport2.MessageCallCount);
        Assert.Equal("Hello", ((TextContent)transport2.ReceivedMessages[0].Contents[0]).Text);
    }
}
