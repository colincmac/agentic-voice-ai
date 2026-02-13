using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.RealtimeVoice.Azure.Calling;
using Agents.AI.RealtimeVoice.Azure.Calling.Models;
using Agents.AI.RealtimeVoice.Azure.Calling.Transports;
using Agents.AI.RealtimeVoice.Azure.Tests.Mocks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.RealtimeVoice.Azure.Tests;

/// <summary>
/// Tests for participant-level routing and transport management
/// </summary>
public class ParticipantRoutingTests
{
    [Fact]
    public void MockChannelTransport_HasCorrectMetadata()
    {
        // Arrange
        var metadata = new ParticipantTransportMetadata
        {
            ContactId = "test-contact",
            ChannelType = CommunicationChannelType.Phone,
            RawIdentifier = "test-identifier",
            DisplayName = "Test Contact",
            SupportsAudio = true,
            SupportsMessaging = false
        };

        // Act
        var transport = new MockChannelTransport("test-channel", metadata);

        // Assert
        Assert.Equal("test-channel", transport.ChannelId);
        Assert.Equal("test-contact", transport.Metadata.ContactId);
        Assert.Equal(CommunicationChannelType.Phone, transport.Metadata.ChannelType);
        Assert.True(transport.Metadata.SupportsAudio);
        Assert.False(transport.Metadata.SupportsMessaging);
    }

    [Fact]
    public async Task MockChannelTransport_TracksAudioSends()
    {
        // Arrange
        var transport = new MockChannelTransport("test-channel");
        var audioData = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        await transport.SendAudioAsync(audioData);
        await transport.SendAudioAsync(audioData);

        // Assert
        Assert.Equal(2, transport.AudioCallCount);
        Assert.Equal(2, transport.ReceivedAudio.Count);
    }

    [Fact]
    public async Task MockChannelTransport_TracksMessageSends()
    {
        // Arrange
        var transport = new MockChannelTransport("test-channel");
        var message = new MessageUpdate
        {
            CreatedAt = DateTimeOffset.UtcNow,
            SenderParticipantId = "sender",
            Role = "user",
            Contents = [new TextContent("Hello")]
        };

        // Act
        await transport.SendMessageAsync(message);

        // Assert
        Assert.Equal(1, transport.MessageCallCount);
        Assert.Single(transport.ReceivedMessages);
        Assert.Equal("Hello", ((TextContent)transport.ReceivedMessages[0].Contents[0]).Text);
    }

    [Fact]
    public async Task MockChannelTransport_InvokesAudioHandler()
    {
        // Arrange
        var transport = new MockChannelTransport("test-channel");
        string? receivedChannelId = null;
        ReadOnlyMemory<byte> receivedAudio = default;

        transport.OnAudioReceived((channelId, audio, ct) =>
        {
            receivedChannelId = channelId;
            receivedAudio = audio;
            return Task.CompletedTask;
        });

        var audioData = new byte[] { 10, 20, 30 };

        // Act
        await transport.SimulateInboundAudioAsync(audioData);

        // Assert
        Assert.Equal("test-channel", receivedChannelId);
        Assert.True(receivedAudio.Span.SequenceEqual(audioData));
    }

    [Fact]
    public async Task MockChannelTransport_InvokesMessageHandler()
    {
        // Arrange
        var transport = new MockChannelTransport("test-channel");
        string? receivedChannelId = null;
        MessageUpdate? receivedMessage = null;

        transport.OnMessageReceived((channelId, message, ct) =>
        {
            receivedChannelId = channelId;
            receivedMessage = message;
            return Task.CompletedTask;
        });

        var message = new MessageUpdate
        {
            CreatedAt = DateTimeOffset.UtcNow,
            SenderParticipantId = "user",
            Role = "user",
            Contents = [new TextContent("Test message")]
        };

        // Act
        await transport.SimulateInboundMessageAsync(message);

        // Assert
        Assert.Equal("test-channel", receivedChannelId);
        Assert.NotNull(receivedMessage);
        Assert.Equal("user", receivedMessage.SenderParticipantId);
    }

    [Fact]
    public async Task MockChannelTransport_Connect_SetsConnected()
    {
        // Arrange
        var transport = new MockChannelTransport("test-channel");

        // Act
        await transport.ConnectAsync();

        // Assert
        Assert.True(transport.WasStarted);
        Assert.True(transport.IsConnected);
    }

    [Fact]
    public async Task MockChannelTransport_Dispose_SetsDisposed()
    {
        // Arrange
        var transport = new MockChannelTransport("test-channel");
        bool disconnectedCalled = false;

        transport.OnDisconnected(_ =>
        {
            disconnectedCalled = true;
            return Task.CompletedTask;
        });

        // Act
        await transport.DisposeAsync();

        // Assert
        Assert.True(transport.WasDisposed);
        Assert.False(transport.IsConnected);
        Assert.True(disconnectedCalled);
    }

    [Fact]
    public async Task MockChannelTransport_DoubleDispose_IsIdempotent()
    {
        // Arrange
        var transport = new MockChannelTransport("test-channel");
        int disconnectedCount = 0;

        transport.OnDisconnected(_ =>
        {
            disconnectedCount++;
            return Task.CompletedTask;
        });

        // Act
        await transport.DisposeAsync();
        await transport.DisposeAsync();

        // Assert
        Assert.Equal(1, disconnectedCount);
    }

    [Fact]
    public void ParticipantTransportMetadata_RequiredProperties()
    {
        // Arrange & Act
        var metadata = new ParticipantTransportMetadata
        {
            ContactId = "contact-123",
            ChannelType = CommunicationChannelType.VoiceAIAgent,
            RawIdentifier = "agent-456",
            DisplayName = "AI Agent",
            SupportsAudio = true,
            SupportsMessaging = true,
            SupportsVideo = false,
            SupportsScreenShare = false
        };

        // Assert
        Assert.Equal("contact-123", metadata.ContactId);
        Assert.Equal(CommunicationChannelType.VoiceAIAgent, metadata.ChannelType);
        Assert.Equal("agent-456", metadata.RawIdentifier);
        Assert.Equal("AI Agent", metadata.DisplayName);
        Assert.True(metadata.SupportsAudio);
        Assert.True(metadata.SupportsMessaging);
        Assert.False(metadata.SupportsVideo);
        Assert.False(metadata.SupportsScreenShare);
    }

    [Fact]
    public void CommunicationChannelType_HasExpectedValues()
    {
        // Assert
        Assert.True(Enum.IsDefined(typeof(CommunicationChannelType), CommunicationChannelType.TeamsChatThread));
        Assert.True(Enum.IsDefined(typeof(CommunicationChannelType), CommunicationChannelType.Phone));
        Assert.True(Enum.IsDefined(typeof(CommunicationChannelType), CommunicationChannelType.ChatAIAgent));
        Assert.True(Enum.IsDefined(typeof(CommunicationChannelType), CommunicationChannelType.VoiceAIAgent));
        Assert.True(Enum.IsDefined(typeof(CommunicationChannelType), CommunicationChannelType.AcsUser));
        Assert.True(Enum.IsDefined(typeof(CommunicationChannelType), CommunicationChannelType.Unknown));
    }

    [Fact]
    public void ParticipantTransportMetadata_OptionalProperties_HaveDefaults()
    {
        // Arrange & Act
        var metadata = new ParticipantTransportMetadata
        {
            ContactId = "contact-123",
            ChannelType = CommunicationChannelType.Unknown,
            RawIdentifier = "raw-id"
        };

        // Assert
        Assert.Null(metadata.DisplayName);
        Assert.Null(metadata.CallConnectionId);
        Assert.Null(metadata.ServerCallId);
        Assert.False(metadata.IsMuted);
        Assert.False(metadata.IsOnHold);
        Assert.Empty(metadata.Metadata);
    }

    [Fact]
    public void ParticipantTransportMetadata_JoinedAt_IsSet()
    {
        // Arrange
        var before = DateTimeOffset.UtcNow;

        // Act
        var metadata = new ParticipantTransportMetadata
        {
            ContactId = "contact-123",
            ChannelType = CommunicationChannelType.Unknown,
            RawIdentifier = "raw-id"
        };

        var after = DateTimeOffset.UtcNow;

        // Assert
        Assert.True(metadata.JoinedAt >= before && metadata.JoinedAt <= after);
    }

    [Fact]
    public async Task ScopedChannelTransport_DoubleDispose_FiresCallbackOnce()
    {
        var inner = new MockChannelTransport("inner-channel");
        var callbackCount = 0;

        var scoped = new ScopedChannelTransport(inner, new ServiceCollection().BuildServiceProvider(), _ =>
        {
            Interlocked.Increment(ref callbackCount);
            return Task.CompletedTask;
        });

        await scoped.DisposeAsync();
        await scoped.DisposeAsync();

        Assert.Equal(1, callbackCount);
        Assert.True(inner.WasDisposed);
    }

    [Fact]
    public async Task ScopedChannelTransport_IsConnected_FalseAfterDispose()
    {
        var inner = new MockChannelTransport("inner-channel");
        var scoped = new ScopedChannelTransport(inner, new ServiceCollection().BuildServiceProvider());
        await scoped.ConnectAsync();

        Assert.True(scoped.IsConnected);

        await scoped.DisposeAsync();

        Assert.False(scoped.IsConnected);
    }

    [Fact]
    public async Task HubSessionParticipantContext_AddTransport_IsThreadSafe()
    {
        await using var scope = new ServiceCollection().BuildServiceProvider().CreateAsyncScope();
        await using var context = new HubSessionParticipantContext(scope, "participant-1");

        var tasks = Enumerable.Range(0, 10).Select(i =>
        {
            var transport = new MockChannelTransport($"channel-{i}");
            return context.AddTransportAsync(transport);
        });

        await Task.WhenAll(tasks);

        Assert.Equal(10, context.Transports.Count);
    }

    [Fact]
    public async Task HubSessionParticipantContext_MetadataCache_InvalidatesOnAddRemove()
    {
        await using var scope = new ServiceCollection().BuildServiceProvider().CreateAsyncScope();
        await using var context = new HubSessionParticipantContext(scope, "participant-1");

        var metadata1 = context.Metadata;
        Assert.False(metadata1.SupportsAudio);

        var transport = new MockChannelTransport("audio-channel", new ParticipantTransportMetadata
        {
            ContactId = "audio-channel",
            ChannelType = CommunicationChannelType.Phone,
            RawIdentifier = "audio-channel",
            SupportsAudio = true,
            SupportsMessaging = false
        });
        await context.AddTransportAsync(transport);

        var metadata2 = context.Metadata;
        Assert.True(metadata2.SupportsAudio);
        Assert.NotSame(metadata1, metadata2);

        await context.RemoveTransport("audio-channel");

        var metadata3 = context.Metadata;
        Assert.False(metadata3.SupportsAudio);
    }

    [Fact]
    public async Task HubSessionParticipantContext_SendMessage_ReachesAllTransports()
    {
        await using var scope = new ServiceCollection().BuildServiceProvider().CreateAsyncScope();
        await using var context = new HubSessionParticipantContext(scope, "participant-1");

        var transport1 = new MockChannelTransport("ch-1");
        var transport2 = new MockChannelTransport("ch-2");

        await context.AddTransportAsync(transport1);
        await context.AddTransportAsync(transport2);

        var message = new MessageUpdate
        {
            CreatedAt = DateTimeOffset.UtcNow,
            SenderParticipantId = "sender",
            Role = "user",
            Contents = [new TextContent("Hello")]
        };

        await context.SendMessageAsync(message);

        Assert.Equal(1, transport1.MessageCallCount);
        Assert.Equal(1, transport2.MessageCallCount);
    }

    [Fact]
    public async Task HubSessionParticipantContext_SendAudio_DistributesToAudioCapableTransports()
    {
        await using var scope = new ServiceCollection().BuildServiceProvider().CreateAsyncScope();
        await using var context = new HubSessionParticipantContext(scope, "participant-1");

        var audioTransport = new MockChannelTransport("audio-ch", new ParticipantTransportMetadata
        {
            ContactId = "audio-ch",
            ChannelType = CommunicationChannelType.Phone,
            RawIdentifier = "audio-ch",
            SupportsAudio = true,
            SupportsMessaging = false
        });
        var chatTransport = new MockChannelTransport("chat-ch", new ParticipantTransportMetadata
        {
            ContactId = "chat-ch",
            ChannelType = CommunicationChannelType.ChatAIAgent,
            RawIdentifier = "chat-ch",
            SupportsAudio = false,
            SupportsMessaging = true
        });

        await context.AddTransportAsync(audioTransport);
        await context.AddTransportAsync(chatTransport);

        var audioData = new byte[] { 1, 2, 3, 4, 5 };
        await context.SendAudioAsync(audioData);

        // Allow time for the RawMediaStreamChannel distribution and pump loop
        await Task.Delay(200);

        Assert.True(audioTransport.AudioCallCount > 0);
        Assert.Equal(0, chatTransport.AudioCallCount);
    }

    [Fact]
    public async Task HubSessionParticipantContext_RemoveTransport_IsIdempotent()
    {
        await using var scope = new ServiceCollection().BuildServiceProvider().CreateAsyncScope();
        await using var context = new HubSessionParticipantContext(scope, "participant-1");

        var transport = new MockChannelTransport("ch-1");
        await context.AddTransportAsync(transport);

        var firstRemove = await context.RemoveTransport("ch-1");
        var secondRemove = await context.RemoveTransport("ch-1");

        Assert.True(firstRemove);
        Assert.False(secondRemove);
    }

    [Fact]
    public async Task HubSessionParticipantContext_DisposeAsync_IsIdempotent()
    {
        var scope = new ServiceCollection().BuildServiceProvider().CreateAsyncScope();
        var context = new HubSessionParticipantContext(scope, "participant-1");
        var disconnectedCount = 0;

        context.OnDisconnected(_ =>
        {
            Interlocked.Increment(ref disconnectedCount);
            return Task.CompletedTask;
        });

        var transport = new MockChannelTransport("ch-1");
        await context.AddTransportAsync(transport);

        await context.DisposeAsync();
        await context.DisposeAsync();

        Assert.Equal(1, disconnectedCount);
        Assert.False(context.IsConnected);
    }
}
