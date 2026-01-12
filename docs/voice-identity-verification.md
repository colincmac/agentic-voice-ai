# Voice Identity Verification Guide

This guide describes how to enable and test voice biometric identity verification in the contact center platform.

## Overview

Voice biometric verification allows the AI agent to verify callers during phone conversations by analyzing their voice patterns. This provides an additional layer of security for sensitive operations like account access, fund transfers, or personal information retrieval.

The system supports two modes:
1. **Stub Mode** (default): In-memory implementation for development and testing
2. **API Mode**: Connects to the real Python gRPC biometrics service

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                       VoiceAgent Session                         │
│                                                                  │
│  ┌─────────────────┐    ┌────────────────────────────────────┐ │
│  │  AI Agent with  │    │      IVoiceBiometricEvaluator       │ │
│  │  VoiceBiometric │───►│                                     │ │
│  │     Tools       │    │  ┌─────────────┬────────────────┐  │ │
│  └─────────────────┘    │  │ Stub Mode   │   API Mode     │  │ │
│                         │  │ (In-memory) │   (gRPC)       │  │ │
│                         │  └─────────────┴────────────────┘  │ │
│                         └────────────────────────────────────┘ │
│                                        │                        │
│                                        │ API Mode               │
│                                        ▼                        │
│                         ┌────────────────────────────────────┐ │
│                         │   Python gRPC Biometrics Service    │ │
│                         │   (SpeechBrain ECAPA-TDNN)          │ │
│                         └────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
```

## Configuration

### Stub Mode (Development)

For development and testing, use the stub implementation:

```csharp
builder.AddConversationHub(...)
    .AddBiometricEvaluation();  // Uses in-memory stub
```

### API Mode (Production)

For production, connect to the real biometrics service:

```csharp
builder.AddConversationHub(...)
    .AddApiBiometricEvaluation();  // Uses gRPC API
```

#### Configuration Options

Add the following to your `appsettings.json`:

```json
{
  "BiometricsApi": {
    "Endpoint": "http://localhost:50051",
    "TimeoutSeconds": 30,
    "VerificationThreshold": 0.25,
    "MinAudioDurationSeconds": 1.0,
    "MaxAudioDurationSeconds": 30.0,
    "Enabled": true,
    "AllowInsecureConnection": true,
    "RetryCount": 3,
    "RetryDelayMilliseconds": 500
  }
}
```

| Option | Default | Description |
|--------|---------|-------------|
| `Endpoint` | (required) | gRPC endpoint URL for the biometrics service |
| `TimeoutSeconds` | 30 | Timeout for gRPC calls |
| `VerificationThreshold` | 0.25 | Minimum similarity score (0.0-1.0) for verification |
| `MinAudioDurationSeconds` | 1.0 | Minimum audio duration required |
| `MaxAudioDurationSeconds` | 30.0 | Maximum audio duration allowed |
| `Enabled` | false | Enable/disable the biometrics API |
| `AllowInsecureConnection` | false | Allow non-TLS connections (dev only) |
| `RetryCount` | 3 | Number of retry attempts |
| `RetryDelayMilliseconds` | 500 | Initial retry delay (exponential backoff) |

## Voice Verification Flow

### 1. Enrollment (First-time setup)

```
┌─────────────┐                    ┌─────────────────┐
│   Caller    │                    │    AI Agent     │
└──────┬──────┘                    └────────┬────────┘
       │                                     │
       │  "I'd like to set up voice ID"     │
       │────────────────────────────────────►│
       │                                     │
       │  "Please say: My voice is my       │
       │   password. Verify me."            │
       │◄────────────────────────────────────│
       │                                     │
       │  [Speaks the phrase]               │
       │────────────────────────────────────►│
       │                                     │
       │  [Audio processed, embedding saved] │
       │                                     │
       │  "Voice enrollment complete!"       │
       │◄────────────────────────────────────│
```

### 2. Verification (Subsequent calls)

```
┌─────────────┐                    ┌─────────────────┐
│   Caller    │                    │    AI Agent     │
└──────┬──────┘                    └────────┬────────┘
       │                                     │
       │  "I need to transfer funds"        │
       │────────────────────────────────────►│
       │                                     │
       │  "For security, I need to verify   │
       │   your voice. Please speak now."   │
       │◄────────────────────────────────────│
       │                                     │
       │  [Speaks naturally]                │
       │────────────────────────────────────►│
       │                                     │
       │  [Voice compared to enrollment]     │
       │  [Confidence: 95%]                  │
       │                                     │
       │  "Voice verified! Processing your  │
       │   transfer request..."              │
       │◄────────────────────────────────────│
```

## Gating Sensitive Tool Actions

Use the `[RequiresVoiceBiometric]` attribute to protect sensitive operations:

```csharp
public class BankingTools
{
    [Description("Transfer funds between accounts")]
    [RequiresVoiceBiometric(confidenceThreshold: 0.9)]
    public async Task<string> TransferFundsAsync(
        string fromAccount,
        string toAccount,
        decimal amount)
    {
        // This will only execute after voice verification
        return $"Transferred ${amount} from {fromAccount} to {toAccount}";
    }
}
```

## AI Agent Tool Reference

The `VoiceBiometricTools` class provides tools for the AI agent:

| Tool | Description |
|------|-------------|
| `CheckVoiceEnrollmentAsync` | Check if a participant has an enrolled voice profile |
| `EnrollVoiceAsync` | Process an audio sample for enrollment |
| `VerifyVoiceAsync` | Verify a voice sample against the enrolled profile |
| `RequestVoiceSampleAsync` | Generate a prompt for the caller to provide audio |
| `CheckBiometricsAvailabilityAsync` | Check if biometrics service is operational |

## Telemetry & Logging

### Activity Tracing

The biometrics integration emits OpenTelemetry activities:

- `BiometricEnrollment` - Voice enrollment operations
- `BiometricVerification` - Voice verification operations
- `VoiceBiometricAuthorization` - Tool authorization checks

### Logged Information

- Enrollment progress and completion
- Verification results and confidence scores
- Service availability issues
- Authorization decisions

Example log entries:

```
info: ApiBiometricEvaluator[0]
      Enrolling voice for participant user-123 with 32000 bytes

info: ApiBiometricEvaluator[0]
      Verification result for participant user-123: Match=True, Confidence=92.35%

warn: VoiceBiometricHandler[0]
      Voice biometric verification confidence 72.00% below threshold 85.00% for participant user-123
```

## Error Handling

### Service Unavailable

When the biometrics service is unavailable:

```
"Voice biometrics service is currently unavailable. Please try again later."
```

The agent should offer alternative verification methods.

### Not Enrolled

When the caller hasn't enrolled their voice:

```
"No voice profile found. Please complete enrollment first."
```

The agent should guide the caller through enrollment.

### Low Confidence

When verification confidence is below threshold:

```
"Voice verification confidence was too low. Please speak more clearly and try again."
```

The agent should request another voice sample.

## Development & Testing

### Running the Python Biometrics Service

```bash
cd src/python-services/voice-biometrics

# Install dependencies
pip install -r requirements.txt

# Generate proto stubs
python -m grpc_tools.protoc -I./protos \
    --python_out=./models \
    --grpc_python_out=./models \
    ./protos/biometrics.proto

# Run the server
python server.py
```

The service will start on:
- gRPC: `localhost:50051`
- HTTP Health: `localhost:8080/health`

### Running with Docker

```bash
cd src/python-services/voice-biometrics

# Build
docker build -t voice-biometrics .

# Run
docker run -p 50051:50051 -p 8080:8080 -v embeddings:/app/embeddings voice-biometrics
```

### Unit Tests

Run the biometrics tests:

```bash
dotnet test test/Agents.AI.RealtimeVoice.Azure.Tests \
    --filter "FullyQualifiedName~ApiBiometricsTests"
```

### Integration Testing

1. Start the Python biometrics service
2. Configure the VoiceAgent with API mode
3. Make test calls through the contact center
4. Verify enrollment and verification flows work end-to-end

## Security Considerations

1. **TLS**: Always use TLS in production (`AllowInsecureConnection: false`)
2. **Threshold Tuning**: Higher thresholds reduce false positives but may increase false negatives
3. **Audio Quality**: Clear audio improves verification accuracy
4. **Enrollment Quality**: Use multiple enrollment samples for better profiles
5. **Fallback Methods**: Always provide alternative verification methods

## Troubleshooting

### Common Issues

| Issue | Solution |
|-------|----------|
| Connection refused | Check biometrics service is running |
| Low confidence scores | Improve audio quality, re-enroll |
| Timeout errors | Increase `TimeoutSeconds` |
| gRPC errors | Check endpoint URL and TLS settings |

### Diagnostic Commands

```bash
# Check service health
curl http://localhost:8080/health

# Test gRPC connection
grpcurl -plaintext localhost:50051 list
```
