using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.RealtimeVoice.Azure.Calling;
using Agents.AI.RealtimeVoice.Azure.Models;
using Microsoft.Extensions.AI;

namespace Agents.AI.RealtimeVoice.Azure.Tests;

public class SessionContextBusTests
{
    [Fact]
    public async Task PublishAsync_DeliversToSubscriber()
    {
        await using var bus = new HubSessionEventBus();
        await using var sub = bus.Subscribe();

        var evt = MakeEvent(HubSessionEventKind.ChatMessage, "p1", "hello");
        await bus.PublishAsync(evt);

        Assert.Equal(1, sub.Available);
    }

    [Fact]
    public async Task PublishAsync_DeliversToMultipleSubscribers()
    {
        await using var bus = new HubSessionEventBus();
        await using var sub1 = bus.Subscribe();
        await using var sub2 = bus.Subscribe();

        await bus.PublishAsync(MakeEvent(HubSessionEventKind.Transcript, "p1", "hi"));

        Assert.Equal(1, sub1.Available);
        Assert.Equal(1, sub2.Available);
    }

    [Fact]
    public async Task Subscribe_WithFilter_OnlyReceivesMatchingEvents()
    {
        await using var bus = new HubSessionEventBus();
        await using var approvalOnly = bus.Subscribe(e => e.Kind is HubSessionEventKind.ApprovalRequest);
        await using var all = bus.Subscribe();

        await bus.PublishAsync(MakeEvent(HubSessionEventKind.ChatMessage, "p1", "chat"));
        await bus.PublishAsync(MakeEvent(HubSessionEventKind.ApprovalRequest, "agent", "approve transfer?"));
        await bus.PublishAsync(MakeEvent(HubSessionEventKind.Transcript, "p1", "transcript"));

        Assert.Equal(1, approvalOnly.Available);
        Assert.Equal(3, all.Available);
    }

    [Fact]
    public async Task EventHistory_RetainsPublishedEvents()
    {
        await using var bus = new HubSessionEventBus();

        await bus.PublishAsync(MakeEvent(HubSessionEventKind.ChatMessage, "p1", "first"));
        await bus.PublishAsync(MakeEvent(HubSessionEventKind.Transcript, "p2", "second"));

        var history = bus.EventHistory;
        Assert.Equal(2, history.Count);
        Assert.Equal(HubSessionEventKind.ChatMessage, history[0].Kind);
        Assert.Equal(HubSessionEventKind.Transcript, history[1].Kind);
    }

    [Fact]
    public async Task EventHistory_TrimsToMaxSize()
    {
        await using var bus = new HubSessionEventBus { MaxHistorySize = 3 };

        for (var i = 0; i < 5; i++)
        {
            await bus.PublishAsync(MakeEvent(HubSessionEventKind.Transcript, "p1", $"msg-{i}"));
        }

        Assert.True(bus.EventHistory.Count <= 3);
    }

    [Fact]
    public async Task PublishAsync_IsNonBlocking_WithDropOldest()
    {
        await using var bus = new HubSessionEventBus();
        await using var sub = bus.Subscribe();

        // Publish more events than the subscriber channel capacity (500)
        for (var i = 0; i < 600; i++)
        {
            await bus.PublishAsync(MakeEvent(HubSessionEventKind.Transcript, "p1", $"frame-{i}"));
        }

        // Should not throw or block — oldest events are dropped
        Assert.True(sub.Available <= 500);
    }

    [Fact]
    public async Task ReadAllAsync_ReceivesPublishedEvents()
    {
        await using var bus = new HubSessionEventBus();
        await using var sub = bus.Subscribe();

        var expected = MakeEvent(HubSessionEventKind.AgentInsight, "screen-agent", "user is viewing dashboard");
        await bus.PublishAsync(expected);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        SessionContextEvent? received = null;

        await foreach (var evt in sub.ReadAllAsync(cts.Token))
        {
            received = evt;
            break;
        }

        Assert.NotNull(received);
        Assert.Equal(HubSessionEventKind.AgentInsight, received.Kind);
        Assert.Equal("screen-agent", received.SourceParticipantId);
    }

    [Fact]
    public async Task TargetParticipantId_CanBeUsedForDirectedEvents()
    {
        await using var bus = new HubSessionEventBus();

        // Subscriber that only accepts events targeted at "supervisor-1"
        await using var supervisorSub = bus.Subscribe(
            e => e.TargetParticipantId is null || e.TargetParticipantId == "supervisor-1");

        // Subscriber for "agent-1"
        await using var agentSub = bus.Subscribe(
            e => e.TargetParticipantId is null || e.TargetParticipantId == "agent-1");

        // Broadcast event (no target) — both receive
        await bus.PublishAsync(MakeEvent(HubSessionEventKind.Transcript, "caller", "hello"));

        // Directed event to supervisor only
        await bus.PublishAsync(new SessionContextEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            Kind = HubSessionEventKind.ApprovalRequest,
            SourceParticipantId = "agent-1",
            TargetParticipantId = "supervisor-1",
            Payload = "Please approve transfer"
        });

        Assert.Equal(2, supervisorSub.Available);
        Assert.Equal(1, agentSub.Available);
    }

    [Fact]
    public async Task Dispose_CompletesSubscriberChannels()
    {
        var bus = new HubSessionEventBus();
        var sub = bus.Subscribe();

        await bus.PublishAsync(MakeEvent(HubSessionEventKind.ChatMessage, "p1", "before dispose"));
        await bus.DisposeAsync();

        var received = new List<SessionContextEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await foreach (var evt in sub.ReadAllAsync(cts.Token))
        {
            received.Add(evt);
        }

        Assert.Single(received);
        Assert.Equal(HubSessionEventKind.ChatMessage, received[0].Kind);
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var bus = new HubSessionEventBus();
        await bus.DisposeAsync();
        await bus.DisposeAsync();
    }

    [Fact]
    public void ChannelRole_Flags_AreCombinableWithBitwiseOr()
    {
        var role = ChannelRole.PrimaryVoice | ChannelRole.InteractiveMessaging;

        Assert.True(role.HasFlag(ChannelRole.PrimaryVoice));
        Assert.True(role.HasFlag(ChannelRole.InteractiveMessaging));
        Assert.False(role.HasFlag(ChannelRole.DataStream));
        Assert.False(role.HasFlag(ChannelRole.ControlPlane));
    }

    [Fact]
    public void ChannelRole_None_IsDefault()
    {
        var metadata = new ParticipantTransportMetadata
        {
            ContactId = "test",
            ChannelType = CommunicationChannelType.Unknown,
            RawIdentifier = "test"
        };

        Assert.Equal(ChannelRole.None, metadata.Role);
    }

    [Fact]
    public void ChannelRole_AggregatesAcrossTransports()
    {
        var voiceRole = ChannelRole.PrimaryVoice;
        var chatRole = ChannelRole.InteractiveMessaging | ChannelRole.ControlPlane;

        var aggregated = voiceRole | chatRole;

        Assert.True(aggregated.HasFlag(ChannelRole.PrimaryVoice));
        Assert.True(aggregated.HasFlag(ChannelRole.InteractiveMessaging));
        Assert.True(aggregated.HasFlag(ChannelRole.ControlPlane));
        Assert.False(aggregated.HasFlag(ChannelRole.DataStream));
    }

    [Fact]
    public void MessageUpdate_TargetParticipantId_IsNullByDefault()
    {
        var msg = new MessageUpdate();
        Assert.Null(msg.TargetParticipantId);
    }

    [Fact]
    public void MessageUpdate_TargetParticipantId_CanBeSet()
    {
        var msg = new MessageUpdate
        {
            SenderParticipantId = "agent-1",
            TargetParticipantId = "supervisor-1",
            Role = "assistant",
            Contents = [new TextContent("Need approval for transfer")]
        };

        Assert.Equal("supervisor-1", msg.TargetParticipantId);
    }

    private static SessionContextEvent MakeEvent(HubSessionEventKind kind, string source, object payload)
    {
        return new SessionContextEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            Kind = kind,
            SourceParticipantId = source,
            Payload = payload
        };
    }
}
