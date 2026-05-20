# Azure Speech Service

Composite service providing both **speech recognition (STT)** and **speech synthesis (TTS)** backed by the Azure Speech SDK.

## Overview

The `AzureSpeechService` orchestrates:

- **`AzureSpeechRecognizer`** — Continuous speech-to-text using push audio input streams
- **`AzureSpeechSynthesizer`** — Text-to-speech with streaming PCM audio output

Both components maintain **pre-warmed connection pools** to minimize first-byte latency and support high-concurrency scenarios.

## Features

- ✅ **Unified configuration** — Single options object for both STT and TTS
- ✅ **Connection pooling** — Reuses warmed WebSocket connections across requests
- ✅ **Streaming API** — Low-latency audio/transcript streaming via `IAsyncEnumerable<T>`
- ✅ **Dependency injection** — First-class DI support with validation
- ✅ **Azure CLI authentication** — Uses `AzureCliCredential` by default
- ✅ **Interim transcripts** — Real-time partial results during recognition
- ✅ **SSML support** — Fine-grained control over synthesis prosody

## Quick Start

### 1. Configuration

Add to `appsettings.json`:

```json
{
  "AzureSpeech": {
    "Endpoint": "https://your-resource.cognitiveservices.azure.com",
    "RecognitionLocale": "en-US",
    "SynthesisVoiceName": "en-US-Ava:DragonHDLatestNeural",
    "SynthesisLocale": "en-US",
    "SynthesisGender": "Female",
    "Concurrency": 4,
    "MaximumRetainedCapacity": 100
  }
}
```

### 2. Register Service

```csharp
using Agents.AI.ContactCenter.DependencyInjection;

// From configuration
services.AddAzureSpeech(configuration);

// Or with inline configuration
services.AddAzureSpeech(options =>
{
    options.Endpoint = new Uri("https://your-resource.cognitiveservices.azure.com");
    options.RecognitionLocale = "en-US";
    options.SynthesisVoiceName = "en-US-Ava:DragonHDLatestNeural";
});
```

### 3. Inject and Use

```csharp
public class MyService
{
    private readonly AzureSpeechService _speechService;

    public MyService(AzureSpeechService speechService)
    {
        _speechService = speechService;
    }

    public async Task SynthesizeSpeech(CancellationToken ct)
    {
        await foreach (var audioFrame in _speechService.SynthesizeAsync("Hello world!", cancellationToken: ct))
        {
            // Process audio frame (send to transport, save to file, etc.)
        }
    }

    public async Task RecognizeSpeech(IAsyncEnumerable<ReadOnlyMemory<byte>> audioStream, CancellationToken ct)
    {
        await foreach (var transcript in _speechService.RecognizeAsync(audioStream, ct))
        {
            Console.WriteLine(transcript.IsFinal ? $"[FINAL] {transcript.Text}" : $"[INTERIM] {transcript.Text}");
        }
    }
}
```

## Configuration Options

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Endpoint` | `Uri` | **required** | Azure Speech service endpoint URL |
| `RecognitionLocale` | `string` | `en-US` | Speech recognition locale |
| `SynthesisVoiceName` | `string` | `en-US-Ava:DragonHDLatestNeural` | TTS voice name |
| `SynthesisLocale` | `string` | `en-US` | TTS locale |
| `SynthesisGender` | `string` | `Female` | TTS voice gender |
| `OutputFormat` | `SpeechSynthesisOutputFormat` | `Raw24Khz16BitMonoPcm` | Audio output format |
| `Concurrency` | `int` | `2` | Number of pre-warmed instances |
| `MaximumRetainedCapacity` | `int` | `100` | Maximum pooled instances |

## Usage Patterns

### Pattern 1: Convenience Methods

The service provides high-level methods that handle lifecycle management:

```csharp
// Synthesize (one-liner)
await foreach (var frame in speechService.SynthesizeAsync("Hello!", cancellationToken: ct))
{
    // Handle frame
}

// Recognize (one-liner)
await foreach (var transcript in speechService.RecognizeAsync(audioStream, ct))
{
    // Handle transcript
}
```

### Pattern 2: Manual Lifecycle

For advanced scenarios requiring fine-grained control:

```csharp
// Create recognizer
await using var recognizer = speechService.CreateRecognizer();

// Consume transcripts in background
var transcriptTask = Task.Run(async () =>
{
    await foreach (var transcript in recognizer.GetTranscriptsAsync(ct))
    {
        ProcessTranscript(transcript);
    }
}, ct);

// Write audio
foreach (var audioChunk in GetAudioChunks())
{
    await recognizer.WriteAudioAsync(audioChunk, ct);
}

// Signal completion
await recognizer.CompleteAsync(ct);
await transcriptTask;
```

### Pattern 3: SSML Synthesis

Use SSML for prosody control:

```csharp
var ssml = @"
<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>
    <voice name='en-US-Ava:DragonHDLatestNeural'>
        <prosody rate='slow' pitch='high'>
            Welcome!
        </prosody>
    </voice>
</speak>";

var synthesizer = speechService.GetSynthesizer();
await foreach (var frame in synthesizer.SynthesizeAsync(ssml, SynthesizerInputFormat.SSML, ct))
{
    // Handle frame
}
```

## Architecture

```
AzureSpeechService
├── CreateRecognizer() ────> AzureSpeechRecognizer (new instance per call)
│                            └── RecognizerPool (shared, pre-warmed)
│
└── GetSynthesizer() ──────> AzureSpeechSynthesizer (singleton)
                             └── SynthesizerPool (shared, pre-warmed)
```

- **Recognizers** are created per-call and disposed after use (stateful, session-scoped)
- **Synthesizer** is shared across all calls (stateless, safe for concurrent use)
- Both use **connection pools** to avoid WebSocket setup latency

## Connection Pooling

### Synthesizer Pool

```csharp
internal sealed class SynthesizerPool
{
    // Pre-warms 'Concurrency' synthesizers on startup
    // Reuses healthy instances after each request
    // Disposes faulted instances (replaced lazily)
}
```

### Recognizer Pool

```csharp
internal sealed class RecognizerPool
{
    // Pre-warms 'Concurrency' recognizers on startup
    // Returned to pool after session completes successfully
    // Discarded if session fails or is canceled
}
```

## Audio Format Requirements

- **Recognition input**: 16 kHz, 16-bit, mono PCM
- **Synthesis output**: Configurable via `OutputFormat` (default: 24 kHz, 16-bit, mono PCM)

## Error Handling

The service propagates errors from the Azure Speech SDK with detailed logging:

```csharp
try
{
    await foreach (var transcript in speechService.RecognizeAsync(audioStream, ct))
    {
        // ...
    }
}
catch (InvalidOperationException ex)
{
    // SDK errors (e.g., authentication, quota, network)
    logger.LogError(ex, "Speech recognition failed");
}
catch (OperationCanceledException)
{
    // Graceful cancellation
}
```

## Observability

The service logs at appropriate levels:

- **Information**: Service initialization, session start/stop
- **Debug**: Pool operations, recognition events (interim/final), first-byte latency
- **Warning**: SDK cancellations, transient errors
- **Error**: SDK errors with error codes/details

Integrate with OpenTelemetry for distributed tracing.

## Testing

Prefer testing against `ISpeechRecognizer` and `ISpeechSynthesizer` interfaces:

```csharp
// Mock for unit tests
var mockSynthesizer = new Mock<ISpeechSynthesizer>();
mockSynthesizer
    .Setup(s => s.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesizerInputFormat>(), It.IsAny<CancellationToken>()))
    .Returns(AsyncEnumerable.Empty<ReadOnlyMemory<byte>>());

// Or use fakes from AI.TestingFramework (if available)
```

## Performance Considerations

1. **Pool Size**: Set `Concurrency` to match expected concurrent sessions (default: 2)
2. **First Request**: ~100-200ms to establish WebSocket (avoided via pre-warming)
3. **Subsequent Requests**: <10ms to acquire from pool
4. **Memory**: Each pooled instance holds an open WebSocket (~1-2 MB overhead)

## Related Types

- `ISpeechRecognizer` — Speech-to-text interface
- `ISpeechSynthesizer` — Text-to-speech interface
- `TranscriptSegment` — Recognition result (text, role, confidence, timestamps)
- `SynthesizerInputFormat` — Text or SSML

## See Also

- [AzureSpeechRecognizer.cs](./AzureSpeechRecognizer.cs)
- [AzureSpeechSynthesizer.cs](./AzureSpeechSynthesizer.cs)
- [AzureSpeechServiceExample.cs](../Samples/AzureSpeechServiceExample.cs)
- [Azure Speech SDK Documentation](https://learn.microsoft.com/azure/cognitive-services/speech-service/)
