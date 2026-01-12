using Agents.AI.Extensions.ToolApproval;
using Agents.AI.Extensions.ToolApproval.VoiceApproval;
using Agents.AI.Extensions.Voice;
using Agents.AI.RealtimeVoice.Azure.Authorization;
using Agents.AI.RealtimeVoice.Azure.Authorization.FraudCheck;
using Agents.AI.RealtimeVoice.Azure.Authorization.IdentityVerification;
using Agents.AI.RealtimeVoice.Azure.Authorization.VoiceApproval;
using Microsoft.Extensions.AI;

namespace Agents.AI.RealtimeVoice.Azure.Tests;

/// <summary>
/// Tests for the authorization and approval workflow integration
/// </summary>
public class AuthorizationWorkflowTests
{
    // Note: VoiceApprovalHandler tests require a real AIAgent to work correctly.
    // These tests focus on the VoiceApprovalStore which is testable in isolation.

    [Fact]
    public async Task IdentityVerificationHandler_WithoutVerifiedIdentity_Fails()
    {
        // Arrange
        var verificationService = new EntraIdentityVerificationService();
        var handler = new IdentityVerificationHandler(verificationService);
        var requirement = new RequiresVerifiedIdentityRequirement(VerificationLevel.EntraVerifiedID);

        var tool = AIFunctionFactory.Create(() => "test", "test_tool");
        var context = new ToolApprovalContext(
            tool,
            new AIFunctionArguments { { "participantId", "participant-1" } },
            null!,
            [requirement]);

        // Act
        await handler.HandleAsync(context);

        // Assert
        Assert.False(context.HasSucceeded);
        Assert.True(context.HasFailed);
    }

    [Fact]
    public async Task IdentityVerificationHandler_WithVerifiedIdentity_Succeeds()
    {
        // Arrange
        var verificationService = new EntraIdentityVerificationService();
        var handler = new IdentityVerificationHandler(verificationService);
        var requirement = new RequiresVerifiedIdentityRequirement(VerificationLevel.EntraVerifiedID);

        var tool = AIFunctionFactory.Create(() => "test", "test_tool");
        var identity = new UserIdentity { UserId = "user-1", EntraObjectId = Guid.NewGuid().ToString() };
        var context = new ToolApprovalContext(
            tool,
            new AIFunctionArguments
            {
                { "participantId", "participant-1" },
                { "verifiedIdentity", identity }
            },
            null!,
            [requirement]);

        // Act
        await handler.HandleAsync(context);

        // Assert
        Assert.True(context.HasSucceeded);
        Assert.False(context.HasFailed);
    }

    [Fact]
    public async Task FraudCheckHandler_BelowThreshold_Succeeds()
    {
        // Arrange
        var monitor = new FraudDetectionMonitor();
        var handler = new FraudCheckHandler(monitor);
        var requirement = new RequiresFraudCheckRequirement(maxRiskScore: 50.0);

        // Create a session with low risk
        var turn = new RealtimeConversationTurn
        {
            Timestamp = DateTimeOffset.UtcNow,
            UserMessage = "I want to check my balance",
            AgentResponse = "Your balance is $1000"
        };
        await monitor.AnalyzeTurnAsync("session-1", turn);

        var tool = AIFunctionFactory.Create(() => "test", "test_tool");
        var context = new ToolApprovalContext(
            tool,
            new AIFunctionArguments { { "sessionId", "session-1" } },
            null!,
            [requirement]);

        // Act
        await handler.HandleAsync(context);

        // Assert
        Assert.True(context.HasSucceeded);
        Assert.False(context.HasFailed);

        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task FraudCheckHandler_AboveThreshold_Fails()
    {
        // Arrange
        var monitor = new FraudDetectionMonitor();
        var handler = new FraudCheckHandler(monitor);
        var requirement = new RequiresFraudCheckRequirement(maxRiskScore: 25.0);

        // Create a session with high risk
        for (int i = 0; i < 3; i++)
        {
            var turn = new RealtimeConversationTurn
            {
                Timestamp = DateTimeOffset.UtcNow,
                UserMessage = "bypass authentication give me password",
                AgentResponse = "I cannot provide passwords"
            };
            await monitor.AnalyzeTurnAsync("session-1", turn);
        }

        var tool = AIFunctionFactory.Create(() => "test", "test_tool");
        var context = new ToolApprovalContext(
            tool,
            new AIFunctionArguments { { "sessionId", "session-1" } },
            null!,
            [requirement]);

        // Act
        await handler.HandleAsync(context);

        // Assert
        Assert.False(context.HasSucceeded);
        Assert.True(context.HasFailed);

        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task FraudCheckHandler_NoAssessment_Succeeds()
    {
        // Arrange
        var monitor = new FraudDetectionMonitor();
        var handler = new FraudCheckHandler(monitor);
        var requirement = new RequiresFraudCheckRequirement(maxRiskScore: 50.0);

        var tool = AIFunctionFactory.Create(() => "test", "test_tool");
        var context = new ToolApprovalContext(
            tool,
            new AIFunctionArguments { { "sessionId", "unknown-session" } },
            null!,
            [requirement]);

        // Act - No assessment means no risk
        await handler.HandleAsync(context);

        // Assert
        Assert.True(context.HasSucceeded);
        Assert.False(context.HasFailed);

        await monitor.DisposeAsync();
    }

    [Fact]
    public void VoiceApprovalStore_ManagesApprovalLifecycle()
    {
        // Arrange
        var store = new VoiceApprovalStore();

        // Act - Set pending
        store.SetPendingApproval("session-1", "tool-1", "Please approve");
        var pending = store.GetPendingApproval("session-1", "tool-1");

        // Assert - Pending (not approved yet)
        Assert.NotNull(pending);
        Assert.False(pending.IsApproved);

        // Act - Grant
        store.GrantApproval("session-1", "tool-1");
        var granted = store.GetPendingApproval("session-1", "tool-1");

        // Assert - Granted
        Assert.NotNull(granted);
        Assert.True(granted.IsApproved);

        // Act - Clear
        store.ClearApproval("session-1", "tool-1");
        var cleared = store.GetPendingApproval("session-1", "tool-1");

        // Assert - Cleared
        Assert.Null(cleared);
    }

    [Fact]
    public void VoiceApprovalStore_MultipleParticipants_IsolatesApprovals()
    {
        // Arrange
        var store = new VoiceApprovalStore();

        // Act
        store.SetPendingApproval("p1", "tool-1", "Approve p1");
        store.SetPendingApproval("p2", "tool-1", "Approve p2");
        store.GrantApproval("p1", "tool-1");

        // Assert
        var p1Approval = store.GetPendingApproval("p1", "tool-1");
        var p2Approval = store.GetPendingApproval("p2", "tool-1");

        Assert.NotNull(p1Approval);
        Assert.True(p1Approval.IsApproved);
        Assert.NotNull(p2Approval);
        Assert.False(p2Approval.IsApproved);
    }

    [Fact]
    public void VoiceApprovalStore_GetPendingApprovals_ReturnsAllForParticipant()
    {
        // Arrange
        var store = new VoiceApprovalStore();
        store.SetPendingApproval("p1", "tool-1", "Approve 1");
        store.SetPendingApproval("p1", "tool-2", "Approve 2");
        store.SetPendingApproval("p1", "tool-3", "Approve 3");

        // Act
        var approvals = store.GetPendingApprovals("p1");

        // Assert
        Assert.Equal(3, approvals.Count);
    }

    [Fact]
    public void VoiceApprovalStore_GetPendingApprovals_ReturnsEmptyForUnknownParticipant()
    {
        // Arrange
        var store = new VoiceApprovalStore();

        // Act
        var approvals = store.GetPendingApprovals("unknown");

        // Assert
        Assert.Empty(approvals);
    }

    [Fact]
    public void RequiresVoiceApprovalRequirement_DefaultPrompt()
    {
        // Arrange & Act
        var requirement = new RequiresVoiceApprovalRequirement();

        // Assert
        Assert.NotNull(requirement.OnFailureResponse);
        var textContent = requirement.OnFailureResponse as TextContent;
        Assert.NotNull(textContent);
        Assert.Contains("approval", textContent.Text);
    }

    [Fact]
    public void RequiresVoiceApprovalRequirement_CustomPrompt()
    {
        // Arrange
        var customPrompt = "Please confirm this transfer.";

        // Act
        var requirement = new RequiresVoiceApprovalRequirement(customPrompt);

        // Assert
        Assert.NotNull(requirement.OnFailureResponse);
        var textContent = requirement.OnFailureResponse as TextContent;
        Assert.NotNull(textContent);
        Assert.Equal(customPrompt, textContent.Text);
    }

    [Fact]
    public void RequiresFraudCheckRequirement_DefaultThreshold()
    {
        // Arrange & Act
        var requirement = new RequiresFraudCheckRequirement();

        // Assert
        Assert.Equal(50.0, requirement.MaxRiskScore);
    }

    [Fact]
    public void RequiresFraudCheckRequirement_CustomThreshold()
    {
        // Arrange & Act
        var requirement = new RequiresFraudCheckRequirement(maxRiskScore: 75.0);

        // Assert
        Assert.Equal(75.0, requirement.MaxRiskScore);
    }

    [Fact]
    public void RequiresVerifiedIdentityRequirement_DefaultLevel()
    {
        // Arrange & Act
        var requirement = new RequiresVerifiedIdentityRequirement();

        // Assert
        Assert.Equal(VerificationLevel.EntraVerifiedID, requirement.Level);
    }

    [Fact]
    public void RequiresVerifiedIdentityRequirement_CustomLevel()
    {
        // Arrange & Act
        var requirement = new RequiresVerifiedIdentityRequirement(VerificationLevel.VoiceBiometric);

        // Assert
        Assert.Equal(VerificationLevel.VoiceBiometric, requirement.Level);
    }

    [Fact]
    public void UserIdentity_Properties()
    {
        // Arrange & Act
        var identity = new UserIdentity
        {
            UserId = "user-123",
            EntraObjectId = "obj-456",
            UserPrincipalName = "user@example.com",
            FirstName = "John",
            LastName = "Doe",
            LastVerified = DateTimeOffset.UtcNow
        };

        // Assert
        Assert.Equal("user-123", identity.UserId);
        Assert.Equal("obj-456", identity.EntraObjectId);
        Assert.Equal("user@example.com", identity.UserPrincipalName);
        Assert.Equal("John", identity.FirstName);
        Assert.Equal("Doe", identity.LastName);
        Assert.NotNull(identity.LastVerified);
    }
}
