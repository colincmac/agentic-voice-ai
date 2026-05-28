using Agents.AI.ContactCenter.Azure;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.DependencyInjection;
using Agents.AI.ContactCenter.Media.Audio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Samples;

/// <summary>
/// Example demonstrating how to configure and use the Azure Speech Service composite
/// for both speech recognition (STT) and synthesis (TTS). The service implements both
/// <see cref="ISpeechRecognizer"/> and <see cref="ISpeechSynthesizer"/> interfaces.
/// </summary>
public static class AzureSpeechServiceExample
{
    /// <summary>
    /// Example 1: Register with dependency injection using configuration.
    /// </summary>
    public static void RegisterFromConfiguration(IServiceCollection services, IConfiguration configuration)
    {
        // Expects configuration section "AzureSpeech" with:
        // {
        //   "Endpoint": "https://your-speech-resource.cognitiveservices.azure.com",
        //   "RecognitionLocale": "en-US",
        //   "SynthesisVoiceName": "en-US-Ava:DragonHDLatestNeural",
        //   "SynthesisLocale": "en-US",
        //   "SynthesisGender": "Female",
        //   "Concurrency": 2,
        //   "MaximumRetainedCapacity": 100
        // }
        services.AddAzureSpeech(configuration);

        // The service is now available as:
        // - AzureSpeechService (concrete type)
        // - ISpeechRecognizer (for STT scenarios)
        // - ISpeechSynthesizer (for TTS scenarios)

        // Or specify a custom configuration section:
        // services.AddAzureSpeech(configuration.GetSection("MyCustomSection"));
    }

    /// <summary>
    /// Example 2: Register with inline configuration.
    /// </summary>
    public static void RegisterWithDelegate(IServiceCollection services)
    {
        services.AddAzureSpeech(options =>
        {
            options.Endpoint = new Uri("https://your-speech-resource.cognitiveservices.azure.com");
            options.RecognitionLocale = "en-US";
            options.SynthesisVoiceName = "en-US-Ava:DragonHDLatestNeural";
            options.SynthesisLocale = "en-US";
            options.SynthesisGender = "Female";
            options.Concurrency = 4;
            options.MaximumRetainedCapacity = 200;
        });
    }

    /// <summary>
    /// Example 3: Register with explicit options.
    /// </summary>
    public static void RegisterWithExplicitOptions(IServiceCollection services)
    {
        var options = new AzureSpeechServiceOptions
        {
            Endpoint = new Uri("https://your-speech-resource.cognitiveservices.azure.com"),
            RecognitionLocale = "en-US",
            SynthesisVoiceName = "en-US-Ava:DragonHDLatestNeural",
            SynthesisLocale = "en-US",
            SynthesisGender = "Female",
            Concurrency = 2
        };

        services.AddAzureSpeech(options);
    }

    /// <summary>
    /// Example 4: Inject as ISpeechSynthesizer (for strategies that only need TTS).
    /// </summary>
    public static async Task UseSynthesizerInterfaceExample(ISpeechSynthesizer synthesizer, CancellationToken cancellationToken)
    {
        var text = "Hello! Welcome to the Azure Speech Service.";

        await foreach (var audioFrame in synthesizer.SynthesizeAsync(text, cancellationToken: cancellationToken))
        {
            // Process audio frame (e.g., send to transport, save to file)
            Console.WriteLine($"Received audio frame: {audioFrame.Length} bytes");
        }
    }

    /// <summary>
    /// Example 5: Inject as ISpeechRecognizer (for strategies that only need STT).
    /// </summary>
    public static async Task UseRecognizerInterfaceExample(ISpeechRecognizer recognizer, CancellationToken cancellationToken)
    {
        // Start consuming transcripts in background
        var transcriptTask = Task.Run(async () =>
        {
            await foreach (var transcript in recognizer.GetTranscriptsAsync(cancellationToken))
            {
                Console.WriteLine(transcript.IsFinal
                    ? $"[FINAL] {transcript.Text}"
                    : $"[INTERIM] {transcript.Text}");
            }
        }, cancellationToken);

        // Simulate audio streaming
        var audioData = new byte[320]; // 20ms @ 16kHz mono
        for (var i = 0; i < 100; i++)
        {
            // In real scenario, this would be actual audio data
            await recognizer.WriteAudioAsync(audioData, cancellationToken);
            await Task.Delay(20, cancellationToken); // Simulate 20ms frames
        }

        // Signal completion
        await recognizer.CompleteAsync(cancellationToken);

        // Wait for all transcripts
        await transcriptTask;
    }

    /// <summary>
    /// Example 6: Inject as concrete type for factory methods.
    /// </summary>
    public static async Task UseConcreteTypeExample(AzureSpeechService speechService, CancellationToken cancellationToken)
    {
        // Create independent recognizer instances
        await using var recognizer1 = speechService.CreateRecognizer();
        await using var recognizer2 = speechService.CreateRecognizer();

        // Get shared synthesizer
        var synthesizer = speechService.GetSynthesizer();

        // Use both
        await foreach (var frame in synthesizer.SynthesizeAsync("Hello from recognizer 1", cancellationToken: cancellationToken))
        {
            await recognizer1.WriteAudioAsync(frame, cancellationToken);
        }
    }

    /// <summary>
    /// Example 7: Contact Center strategy pattern (inject as ISpeechSynthesizer).
    /// </summary>
    public class MyContactCenterStrategy
    {
        private readonly ISpeechSynthesizer _synthesizer;

        // The strategy only knows about the interface, not the concrete implementation
        public MyContactCenterStrategy(ISpeechSynthesizer synthesizer)
        {
            _synthesizer = synthesizer;
        }

        public async Task SpeakGreetingAsync(CancellationToken ct)
        {
            await foreach (var frame in _synthesizer.SynthesizeAsync("Welcome to our service!", cancellationToken: ct))
            {
                // Send to caller
            }
        }
    }

    /// <summary>
    /// Example 8: Full round-trip example with dependency injection.
    /// </summary>
    public static async Task FullRoundTripExample()
    {
        // Setup
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());
        services.AddAzureSpeech(options =>
        {
            options.Endpoint = new Uri("https://your-speech-resource.cognitiveservices.azure.com");
            options.RecognitionLocale = "en-US";
            options.SynthesisVoiceName = "en-US-Ava:DragonHDLatestNeural";
        });

        await using var provider = services.BuildServiceProvider();

        // Can resolve as any of these:
        var speechService = provider.GetRequiredService<AzureSpeechService>();
        var synthesizer = provider.GetRequiredService<ISpeechSynthesizer>();
        var recognizer = provider.GetRequiredService<ISpeechRecognizer>();

        // All three resolve to the same singleton instance
        Console.WriteLine($"Same instance: {ReferenceEquals(speechService, synthesizer)}"); // True
        Console.WriteLine($"Same instance: {ReferenceEquals(speechService, recognizer)}"); // True

        var cancellationToken = CancellationToken.None;

        // Synthesize speech
        Console.WriteLine("Synthesizing speech...");
        var audioFrames = new List<ReadOnlyMemory<byte>>();
        await foreach (var frame in synthesizer.SynthesizeAsync("Please say your name.", cancellationToken: cancellationToken))
        {
            audioFrames.Add(frame);
        }
        Console.WriteLine($"Synthesized {audioFrames.Count} audio frames");

        // Recognize speech (write audio to the same instance cast as ISpeechRecognizer)
        Console.WriteLine("Recognizing speech...");
        var transcriptTask = Task.Run(async () =>
        {
            await foreach (var transcript in recognizer.GetTranscriptsAsync(cancellationToken))
            {
                Console.WriteLine($"{(transcript.IsFinal ? "FINAL" : "INTERIM")}: {transcript.Text}");
            }
        }, cancellationToken);

        foreach (var frame in audioFrames)
        {
            await recognizer.WriteAudioAsync(frame, cancellationToken);
        }

        await recognizer.CompleteAsync(cancellationToken);
        await transcriptTask;
    }
}
