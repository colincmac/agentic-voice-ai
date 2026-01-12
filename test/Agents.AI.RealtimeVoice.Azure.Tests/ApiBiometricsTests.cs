using Agents.AI.Extensions.ToolApproval;
using Agents.AI.RealtimeVoice.Azure.Authorization.Biometrics;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agents.AI.RealtimeVoice.Azure.Tests;

/// <summary>
/// Tests for API biometrics integration, VoiceBiometricHandler, and VoiceBiometricTools.
/// </summary>
public class ApiBiometricsTests
{
    [Fact]
    public void BiometricsApiOptions_HasCorrectDefaults()
    {
        // Arrange & Act
        var options = new VoiceBiometricsApiOptions();

        // Assert
        Assert.Equal(30, options.TimeoutSeconds);
        Assert.Equal(0.25, options.VerificationThreshold);
        Assert.Equal(1.0, options.MinAudioDurationSeconds);
        Assert.Equal(30.0, options.MaxAudioDurationSeconds);
        Assert.True(options.Enabled);
        Assert.False(options.AllowInsecureConnection);
        Assert.Equal(3, options.RetryCount);
        Assert.Equal(500, options.RetryDelayMilliseconds);
    }

    [Fact]
    public void BiometricsApiOptions_CanBeConfigured()
    {
        // Arrange & Act
        var options = new VoiceBiometricsApiOptions
        {
            Endpoint = "https://biometrics.example.com:50051",
            TimeoutSeconds = 60,
            VerificationThreshold = 0.8,
            MinAudioDurationSeconds = 2.0,
            MaxAudioDurationSeconds = 15.0,
            Enabled = true,
            AllowInsecureConnection = true,
            RetryCount = 5,
            RetryDelayMilliseconds = 1000
        };

        // Assert
        Assert.Equal("https://biometrics.example.com:50051", options.Endpoint);
        Assert.Equal(60, options.TimeoutSeconds);
        Assert.Equal(0.8, options.VerificationThreshold);
        Assert.Equal(2.0, options.MinAudioDurationSeconds);
        Assert.Equal(15.0, options.MaxAudioDurationSeconds);
        Assert.True(options.Enabled);
        Assert.True(options.AllowInsecureConnection);
        Assert.Equal(5, options.RetryCount);
        Assert.Equal(1000, options.RetryDelayMilliseconds);
    }

    [Fact]
    public async Task VoiceBiometricHandler_WithVerificationResult_AboveThreshold_Succeeds()
    {
        // Arrange
        await using var evaluator = new VoiceBiometricEvaluator();
        var handler = new VoiceBiometricHandler(evaluator);
        var requirement = new RequiresVoiceBiometricRequirement(confidenceThreshold: 0.8);

        var verificationResult = new VoiceVerificationResult
        {
            Success = true,
            IsMatch = true,
            ConfidenceScore = 0.95,
            VerifiedAt = DateTimeOffset.UtcNow
        };

        var tool = AIFunctionFactory.Create(() => "test", "test_tool");
        var context = new ToolApprovalContext(
            tool,
            new AIFunctionArguments
            {
                { "participantId", "participant-1" },
                { "voiceVerificationResult", verificationResult }
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
    public async Task VoiceBiometricHandler_WithVerificationResult_BelowThreshold_Fails()
    {
        // Arrange
        await using var evaluator = new VoiceBiometricEvaluator();
        var handler = new VoiceBiometricHandler(evaluator);
        var requirement = new RequiresVoiceBiometricRequirement(confidenceThreshold: 0.9);

        var verificationResult = new VoiceVerificationResult
        {
            Success = true,
            IsMatch = true,
            ConfidenceScore = 0.75, // Below threshold
            VerifiedAt = DateTimeOffset.UtcNow
        };

        var tool = AIFunctionFactory.Create(() => "test", "test_tool");
        var context = new ToolApprovalContext(
            tool,
            new AIFunctionArguments
            {
                { "participantId", "participant-1" },
                { "voiceVerificationResult", verificationResult }
            },
            null!,
            [requirement]);

        // Act
        await handler.HandleAsync(context);

        // Assert
        Assert.False(context.HasSucceeded);
        Assert.True(context.HasFailed);

    }

    [Fact]
    public async Task VoiceBiometricHandler_WithNoMatch_Fails()
    {
        // Arrange
        await using var evaluator = new VoiceBiometricEvaluator();
        var handler = new VoiceBiometricHandler(evaluator);
        var requirement = new RequiresVoiceBiometricRequirement(confidenceThreshold: 0.8);

        var verificationResult = new VoiceVerificationResult
        {
            Success = true,
            IsMatch = false,
            ConfidenceScore = 0.4
        };

        var tool = AIFunctionFactory.Create(() => "test", "test_tool");
        var context = new ToolApprovalContext(
            tool,
            new AIFunctionArguments
            {
                { "participantId", "participant-1" },
                { "voiceVerificationResult", verificationResult }
            },
            null!,
            [requirement]);

        // Act
        await handler.HandleAsync(context);

        // Assert
        Assert.False(context.HasSucceeded);
        Assert.True(context.HasFailed);

    }

    [Fact]
    public async Task VoiceBiometricHandler_WithLegacyVoiceVerifiedFlag_Succeeds()
    {
        // Arrange
        await using var evaluator = new VoiceBiometricEvaluator();
        var handler = new VoiceBiometricHandler(evaluator);
        var requirement = new RequiresVoiceBiometricRequirement(confidenceThreshold: 0.8);

        var tool = AIFunctionFactory.Create(() => "test", "test_tool");
        var context = new ToolApprovalContext(
            tool,
            new AIFunctionArguments
            {
                { "participantId", "participant-1" },
                { "voiceVerified", true },
                { "voiceConfidence", 0.95 }
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
    public async Task VoiceBiometricHandler_WithoutVerification_Fails()
    {
        // Arrange
        await using var evaluator = new VoiceBiometricEvaluator();
        var handler = new VoiceBiometricHandler(evaluator);
        var requirement = new RequiresVoiceBiometricRequirement(confidenceThreshold: 0.8);

        var tool = AIFunctionFactory.Create(() => "test", "test_tool");
        var context = new ToolApprovalContext(
            tool,
            new AIFunctionArguments
            {
                { "participantId", "participant-1" }
            },
            null!,
            [requirement]);

        // Act
        await handler.HandleAsync(context);

        // Assert
        Assert.False(context.HasSucceeded);
        Assert.True(context.HasFailed);

    }

    [Fact]
    public async Task VoiceBiometricTools_CheckEnrollment_ReturnsStatus()
    {
        // Arrange
        var options = new VoiceBiometricOptions { MinimumEnrollmentSamples = 2 };
        await using var evaluator = new VoiceBiometricEvaluator(options);
        var tools = new VoiceBiometricTools(evaluator);

        // Enroll some samples
        var audioSample = new byte[1024];
        await evaluator.EnrollVoiceAsync("participant-1", audioSample);

        // Act
        var status = await tools.CheckVoiceEnrollmentAsync("participant-1");

        // Assert
        Assert.False(status.IsEnrolled); // Not complete yet
        Assert.Equal(1, status.EnrollmentSamples);

    }

    [Fact]
    public async Task VoiceBiometricTools_EnrollVoice_WithEmptyAudio_ReturnsError()
    {
        // Arrange
        await using var evaluator = new VoiceBiometricEvaluator();
        var tools = new VoiceBiometricTools(evaluator);

        // Act
        var result = await tools.EnrollVoiceAsync("participant-1", []);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("No audio sample", result.ErrorMessage);

    }

    [Fact]
    public async Task VoiceBiometricTools_VerifyVoice_ReturnsResult()
    {
        // Arrange
        var options = new VoiceBiometricOptions { MinimumEnrollmentSamples = 1 };
        await using var evaluator = new VoiceBiometricEvaluator(options);
        var tools = new VoiceBiometricTools(evaluator);
        var audioSample = new byte[1024];

        // First enroll
        await evaluator.EnrollVoiceAsync("participant-1", audioSample);

        // Act
        var result = await tools.VerifyVoiceAsync("participant-1", audioSample);

        // Assert
        Assert.True(result.Success);
        Assert.NotEmpty(result.Message);

    }

    [Fact]
    public async Task VoiceBiometricTools_VerifyVoice_NotEnrolled_ReturnsHelpfulMessage()
    {
        // Arrange
        await using var evaluator = new VoiceBiometricEvaluator();
        var tools = new VoiceBiometricTools(evaluator);
        var audioSample = new byte[1024];

        // Act
        var result = await tools.VerifyVoiceAsync("unknown-participant", audioSample);

        // Assert
        Assert.False(result.Success);
        // The error pattern is matched and we get a user-friendly message about enrollment
        Assert.Contains("enrolled", result.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task VoiceBiometricTools_RequestVoiceSample_ReturnsEnrollmentPrompt()
    {
        // Arrange
        await using var evaluator = new VoiceBiometricEvaluator();
        var tools = new VoiceBiometricTools(evaluator);

        // Act
        var prompt = await tools.RequestVoiceSampleAsync("participant-1", "enrollment");

        // Assert
        Assert.Contains("voice verification", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("phrase", prompt, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task VoiceBiometricTools_RequestVoiceSample_ReturnsVerificationPrompt()
    {
        // Arrange
        await using var evaluator = new VoiceBiometricEvaluator();
        var tools = new VoiceBiometricTools(evaluator);

        // Act
        var prompt = await tools.RequestVoiceSampleAsync("participant-1", "verification");

        // Assert
        Assert.Contains("verify", prompt, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task VoiceBiometricTools_CheckAvailability_ReturnsStatus()
    {
        // Arrange
        await using var evaluator = new VoiceBiometricEvaluator();
        var tools = new VoiceBiometricTools(evaluator);

        // Act
        var availability = await tools.CheckBiometricsAvailabilityAsync();

        // Assert
        Assert.True(availability.IsAvailable);
        Assert.Contains("available", availability.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void RequiresVoiceBiometricAttribute_CreatesRequirement()
    {
        // Arrange & Act
        var attribute = new RequiresVoiceBiometricAttribute(0.9);
        var requirements = attribute.GetRequirements().ToList();

        // Assert
        Assert.Single(requirements);
        var requirement = requirements[0] as RequiresVoiceBiometricRequirement;
        Assert.NotNull(requirement);
        Assert.Equal(0.9, requirement.ConfidenceThreshold);
    }

    [Fact]
    public void RequiresVoiceBiometricRequirement_HasFailureResponse()
    {
        // Arrange & Act
        var requirement = new RequiresVoiceBiometricRequirement(0.85);

        // Assert
        Assert.NotNull(requirement.OnFailureResponse);
        var textContent = requirement.OnFailureResponse as TextContent;
        Assert.NotNull(textContent);
        Assert.Contains("voice biometric", textContent.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VoiceEnrollmentStatusResult_HasAllProperties()
    {
        // Arrange & Act
        var result = new VoiceEnrollmentStatusResult
        {
            IsEnrolled = true,
            EnrollmentSamples = 3,
            LastEnrolledAt = DateTimeOffset.UtcNow,
            VerificationAttempts = 5,
            SuccessfulVerifications = 4
        };

        // Assert
        Assert.True(result.IsEnrolled);
        Assert.Equal(3, result.EnrollmentSamples);
        Assert.NotNull(result.LastEnrolledAt);
        Assert.Equal(5, result.VerificationAttempts);
        Assert.Equal(4, result.SuccessfulVerifications);
    }

    [Fact]
    public void VoiceVerificationToolResult_HasAllProperties()
    {
        // Arrange & Act
        var result = new VoiceVerificationToolResult
        {
            Success = true,
            IsMatch = true,
            ConfidenceScore = 0.95,
            VerifiedAt = DateTimeOffset.UtcNow,
            Message = "Verified successfully"
        };

        // Assert
        Assert.True(result.Success);
        Assert.True(result.IsMatch);
        Assert.Equal(0.95, result.ConfidenceScore);
        Assert.NotNull(result.VerifiedAt);
        Assert.Equal("Verified successfully", result.Message);
    }

    [Fact]
    public void BiometricsAvailabilityResult_HasAllProperties()
    {
        // Arrange & Act
        var result = new BiometricsAvailabilityResult
        {
            IsAvailable = true,
            Message = "Service is operational"
        };

        // Assert
        Assert.True(result.IsAvailable);
        Assert.Equal("Service is operational", result.Message);
    }
}
