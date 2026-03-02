using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.RealtimeVoice.Azure.Models;
using Agents.AI.RealtimeVoice.Azure.Tests.Mocks;
using Microsoft.Extensions.AI;

namespace Agents.AI.RealtimeVoice.Azure.Tests;

/// <summary>
/// Tests for internal messaging and message update handling
/// </summary>
public class InternalMessagingTests
{
    [Fact]
    public void MessageUpdate_CanBeCreated()
    {
        // Arrange & Act
        var message = new MessageUpdate
        {
            CreatedAt = DateTimeOffset.UtcNow,
            SenderParticipantId = "sender-123",
            Role = "user",
            Contents = [new TextContent("Hello, world!")]
        };

        // Assert
        Assert.Equal("sender-123", message.SenderParticipantId);
        Assert.Equal("user", message.Role);
        Assert.Single(message.Contents);
    }

    [Fact]
    public void MessageUpdate_WithMultipleContents()
    {
        // Arrange & Act
        var message = new MessageUpdate
        {
            CreatedAt = DateTimeOffset.UtcNow,
            SenderParticipantId = "sender-123",
            Role = "assistant",
            Contents =
            [
                new TextContent("First message"),
                new TextContent("Second message")
            ]
        };

        // Assert
        Assert.Equal(2, message.Contents.Count);
        Assert.Equal("First message", ((TextContent)message.Contents[0]).Text);
        Assert.Equal("Second message", ((TextContent)message.Contents[1]).Text);
    }

    [Fact]
    public void TextContent_StoresText()
    {
        // Arrange & Act
        var content = new TextContent("Test message content");

        // Assert
        Assert.Equal("Test message content", content.Text);
    }

    [Fact]
    public async Task MockChannelTransport_ReceivesMessages()
    {
        // Arrange
        var transport = new MockChannelTransport("test-channel");
        var message = new MessageUpdate
        {
            CreatedAt = DateTimeOffset.UtcNow,
            SenderParticipantId = "user",
            Role = "user",
            Contents = [new TextContent("User message")]
        };

        // Act
        await transport.SendMessageAsync(message);

        // Assert
        Assert.Single(transport.ReceivedMessages);
        Assert.Equal("User message", ((TextContent)transport.ReceivedMessages[0].Contents[0]).Text);
    }

    [Fact]
    public async Task MockChannelTransport_InternalRole_IsJustAString()
    {
        // Arrange - The "internal" role is just a string convention
        var transport = new MockChannelTransport("test-channel");
        var internalMessage = new MessageUpdate
        {
            CreatedAt = DateTimeOffset.UtcNow,
            SenderParticipantId = "system",
            Role = "internal",
            Contents = [new TextContent("This is an internal coordination message")]
        };

        // Act
        await transport.SendMessageAsync(internalMessage);

        // Assert
        Assert.Single(transport.ReceivedMessages);
        Assert.Equal("internal", transport.ReceivedMessages[0].Role);
    }

    [Fact]
    public async Task MockChannelTransport_HandlesMultipleMessages()
    {
        // Arrange
        var transport = new MockChannelTransport("test-channel");

        // Act
        for (int i = 0; i < 5; i++)
        {
            var message = new MessageUpdate
            {
                CreatedAt = DateTimeOffset.UtcNow,
                SenderParticipantId = "user",
                Role = "user",
                Contents = [new TextContent($"Message {i}")]
            };
            await transport.SendMessageAsync(message);
        }

        // Assert
        Assert.Equal(5, transport.MessageCallCount);
        Assert.Equal(5, transport.ReceivedMessages.Count);
    }

    [Fact]
    public async Task MockChannelTransport_MessageHandler_ReceivesAllMessages()
    {
        // Arrange
        var transport = new MockChannelTransport("test-channel");
        var receivedMessages = new List<MessageUpdate>();

        transport.SetOnMessageReceived((channelId, message, ct) =>
        {
            receivedMessages.Add(message);
            return Task.CompletedTask;
        });

        // Act
        for (int i = 0; i < 3; i++)
        {
            var message = new MessageUpdate
            {
                CreatedAt = DateTimeOffset.UtcNow,
                SenderParticipantId = "user",
                Role = "user",
                Contents = [new TextContent($"Message {i}")]
            };
            await transport.SimulateInboundMessageAsync(message);
        }

        // Assert
        Assert.Equal(3, receivedMessages.Count);
    }

    [Fact]
    public async Task MockChannelTransport_NoHandler_DoesNotThrow()
    {
        // Arrange
        var transport = new MockChannelTransport("test-channel");
        var message = new MessageUpdate
        {
            CreatedAt = DateTimeOffset.UtcNow,
            SenderParticipantId = "user",
            Role = "user",
            Contents = [new TextContent("Test")]
        };

        // Act & Assert - Should not throw even without a handler
        await transport.SimulateInboundMessageAsync(message);
    }

    [Fact]
    public void MessageUpdate_EmptyContents()
    {
        // Arrange & Act
        var message = new MessageUpdate
        {
            CreatedAt = DateTimeOffset.UtcNow,
            SenderParticipantId = "sender",
            Role = "user",
            Contents = []
        };

        // Assert
        Assert.Empty(message.Contents);
    }

    [Fact]
    public void MockChannelTransport_DefaultMetadata_SupportsMessaging()
    {
        // Arrange & Act
        var transport = new MockChannelTransport("test-channel");

        // Assert
        Assert.True(transport.Metadata.SupportsMessaging);
    }

    [Fact]
    public void MockChannelTransport_CustomMetadata_MessagingDisabled()
    {
        // Arrange
        var metadata = new ParticipantTransportMetadata
        {
            ContactId = "test",
            ChannelType = CommunicationChannelType.Phone,
            RawIdentifier = "test",
            SupportsAudio = true,
            SupportsMessaging = false
        };

        // Act
        var transport = new MockChannelTransport("test-channel", metadata);

        // Assert
        Assert.False(transport.Metadata.SupportsMessaging);
        Assert.True(transport.Metadata.SupportsAudio);
    }
}
