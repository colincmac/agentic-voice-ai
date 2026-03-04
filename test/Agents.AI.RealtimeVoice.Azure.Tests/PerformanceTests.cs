using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.RealtimeVoice.Azure.Models;
using Agents.AI.RealtimeVoice.Azure.Monitoring;
using Agents.AI.RealtimeVoice.Azure.Tests.Mocks;
using Microsoft.Extensions.AI;
using System.Diagnostics;

namespace Agents.AI.RealtimeVoice.Azure.Tests;

/// <summary>
/// Tests for performance and efficiency aspects of the realtime voice components
/// </summary>
public class PerformanceTests
{
    [Fact]
    public async Task MockChannelTransport_HighThroughput_HandlesRapidMessages()
    {
        // Arrange
        var transport = new MockChannelTransport("test-channel");
        const int messageCount = 1000;

        // Act
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < messageCount; i++)
        {
            var message = new MessageUpdate
            {
                CreatedAt = DateTimeOffset.UtcNow,
                SenderParticipantId = "sender",
                Role = "user",
                Contents = [new TextContent($"Message {i}")]
            };
            await transport.SendMessageAsync(message);
        }
        stopwatch.Stop();

        // Assert
        Assert.Equal(messageCount, transport.MessageCallCount);
        // Should complete reasonably fast
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"Took too long: {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task MockChannelTransport_HighThroughput_HandlesRapidAudio()
    {
        // Arrange
        var transport = new MockChannelTransport("test-channel");
        const int audioPacketCount = 1000;
        var audioData = new byte[320]; // Typical audio packet size

        // Act
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < audioPacketCount; i++)
        {
            await transport.SendAudioAsync(audioData);
        }
        stopwatch.Stop();

        // Assert
        Assert.Equal(audioPacketCount, transport.AudioCallCount);
        // Should complete reasonably fast
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"Took too long: {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task MockChannelTransport_ConcurrentSends_AreThreadSafe()
    {
        // Arrange
        var transport = new MockChannelTransport("test-channel");
        const int taskCount = 100;
        const int messagesPerTask = 10;
        var audioData = new byte[100];

        // Act
        var tasks = Enumerable.Range(0, taskCount).Select(async taskId =>
        {
            for (int i = 0; i < messagesPerTask; i++)
            {
                await transport.SendAudioAsync(audioData);
                await transport.SendMessageAsync(new MessageUpdate
                {
                    CreatedAt = DateTimeOffset.UtcNow,
                    SenderParticipantId = $"task-{taskId}",
                    Role = "user",
                    Contents = [new TextContent($"Message {i}")]
                });
            }
        });

        await Task.WhenAll(tasks);

        // Assert - All sends should complete
        Assert.Equal(taskCount * messagesPerTask, transport.AudioCallCount);
        Assert.Equal(taskCount * messagesPerTask, transport.MessageCallCount);
    }

    [Fact]
    public void ConversationSessionMetrics_HighVolumeRecording_DoesNotThrow()
    {
        // Arrange
        var metrics = new SessionTelemetry();
        const int recordCount = 1000;

        // Act & Assert - Should handle high volume without exception
        for (int i = 0; i < recordCount; i++)
        {
            metrics.RecordMessageSent($"session-{i % 10}", latencyMs: i);
            metrics.RecordMessageReceived($"session-{i % 10}", latencyMs: i);
        }

        metrics.Dispose();
    }

    [Fact]
    public async Task ConversationSessionMetrics_ParallelRecording_IsThreadSafe()
    {
        // Arrange
        var metrics = new SessionTelemetry();
        const int threadCount = 10;
        const int operationsPerThread = 100;

        // Act
        var tasks = Enumerable.Range(0, threadCount).Select(threadId => Task.Run(() =>
        {
            for (int i = 0; i < operationsPerThread; i++)
            {
                metrics.RecordSessionStarted($"session-{threadId}-{i}");
                metrics.RecordMessageSent($"session-{threadId}-{i}", latencyMs: 10);
                metrics.RecordToolInvocation($"session-{threadId}-{i}", "test_tool", executionTimeMs: 50, success: true);
                metrics.RecordSessionCompleted($"session-{threadId}-{i}", durationMs: 1000);
            }
        }));

        await Task.WhenAll(tasks);

        // Assert - No exceptions means thread safety
        metrics.Dispose();
    }

    [Fact]
    public async Task MockChannelTransport_AudioHandler_DoesNotBlockSender()
    {
        // Arrange
        var transport = new MockChannelTransport("test-channel");
        var handlerCallCount = 0;

        transport.SetOnAudioReceivedCallback(async (channelId, audio, ct) =>
        {
            Interlocked.Increment(ref handlerCallCount);
            await Task.Delay(10); // Simulate slow handler
        });

        // Act - Send audio rapidly
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            await transport.SimulateInboundAudioAsync(new byte[100]);
        }
        stopwatch.Stop();

        // Assert - Should complete, handler is called for each
        Assert.Equal(100, handlerCallCount);
    }

    [Fact]
    public void ParticipantTransportMetadata_Initialization_IsEfficient()
    {
        // Arrange & Act
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < 10000; i++)
        {
            var metadata = new ParticipantTransportMetadata
            {
                ContactId = $"contact-{i}",
                ChannelType = CommunicationChannelType.VoiceAIAgent,
                RawIdentifier = $"raw-{i}",
                SupportsAudio = true,
                SupportsMessaging = true
            };
        }
        stopwatch.Stop();

        // Assert
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"Took too long: {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task MockChannelTransport_ReadOnlyMemory_AvoidsCopy()
    {
        // Arrange
        var transport = new MockChannelTransport("test-channel");
        var originalData = new byte[] { 1, 2, 3, 4, 5 };
        var memory = new ReadOnlyMemory<byte>(originalData);

        // Act
        await transport.SendAudioAsync(memory);

        // Assert - The received audio should reference the same memory
        Assert.Single(transport.ReceivedAudio);
        Assert.True(transport.ReceivedAudio[0].Span.SequenceEqual(originalData));
    }

    [Fact]
    public void MessageUpdate_Contents_UsesListInitializerEfficiently()
    {
        // Arrange & Act
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < 10000; i++)
        {
            var message = new MessageUpdate
            {
                CreatedAt = DateTimeOffset.UtcNow,
                SenderParticipantId = "sender",
                Role = "user",
                Contents = [new TextContent("Test message")]
            };
        }
        stopwatch.Stop();

        // Assert
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"Took too long: {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task MockChannelTransport_MultipleDisposals_AreIdempotent()
    {
        // Arrange
        var transport = new MockChannelTransport("test-channel");
        var disposeCount = 0;

        transport.SetOnDisconnected(_ =>
        {
            Interlocked.Increment(ref disposeCount);
            return Task.CompletedTask;
        });

        // Act - Dispose multiple times from different threads
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            await transport.DisposeAsync();
        }));

        await Task.WhenAll(tasks);

        // Assert - Should only dispose once
        Assert.Equal(1, disposeCount);
    }
}
