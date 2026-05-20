using Agents.AI.ContactCenter.Azure;
using Agents.AI.ContactCenter.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Samples;

/// <summary>
/// Example demonstrating how to configure and use the Azure Speech Service composite
/// for both speech recognition (STT) and synthesis (TTS).
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
    /// Example 4: Use the service for text-to-speech synthesis.
    /// </summary>
    public static async Task SynthesizeSpeechExample(AzureSpeechService speechService, CancellationToken cancellationToken)
    {
        var text = "Hello! Welcome to the Azure Speech Service.";

        // Using the convenience method
        await foreach (var audioFrame in speechService.SynthesizeAsync(text, cancellationToken: cancellationToken))
        {
            // Process audio frame (e.g., send to transport, save to file)
            Console.WriteLine($"Received audio frame: {audioFrame.Length} bytes");
        }
    }

    /// <summary>
    /// Example 5: Use the service for speech recognition (speech-to-text).
    /// </summary>
    public static async Task RecognizeSpeechExample(
        AzureSpeechService speechService,
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioStream,
        CancellationToken cancellationToken)
    {
        // Using the convenience method
        await foreach (var transcript in speechService.RecognizeAsync(audioStream, cancellationToken))
        {
            if (transcript.IsFinal)
            {
                Console.WriteLine($"[FINAL] {transcript.Text}");
            }
            else
            {
                Console.WriteLine($"[INTERIM] {transcript.Text}");
            }
        }
    }

    /// <summary>
    /// Example 6: Use the service with manual recognizer lifecycle management.
    /// </summary>
    public static async Task RecognizeSpeechWithManualLifecycleExample(
        AzureSpeechService speechService,
        CancellationToken cancellationToken)
    {
        await using var recognizer = speechService.CreateRecognizer();

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
    /// Example 7: Use the service with manual synthesizer access.
    /// </summary>
    public static async Task SynthesizeSpeechWithManualAccessExample(
        AzureSpeechService speechService,
        CancellationToken cancellationToken)
    {
        var synthesizer = speechService.GetSynthesizer();

        // Use SSML for more control
        var ssml = @"
<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>
    <voice name='en-US-Ava:DragonHDLatestNeural'>
        <prosody rate='slow' pitch='low'>
            Welcome to our service.
        </prosody>
        <break time='500ms'/>
        How may I help you today?
    </voice>
</speak>";

        await foreach (var audioFrame in synthesizer.SynthesizeAsync(
            ssml,
            Media.Audio.SynthesizerInputFormat.SSML,
            cancellationToken))
        {
            Console.WriteLine($"Received audio frame: {audioFrame.Length} bytes");
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
        var speechService = provider.GetRequiredService<AzureSpeechService>();
        var cancellationToken = CancellationToken.None;

        // Synthesize speech
        Console.WriteLine("Synthesizing speech...");
        var audioFrames = new List<ReadOnlyMemory<byte>>();
        await foreach (var frame in speechService.SynthesizeAsync("Please say your name.", cancellationToken: cancellationToken))
        {
            audioFrames.Add(frame);
        }
        Console.WriteLine($"Synthesized {audioFrames.Count} audio frames");

        // Recognize speech (simulated with the synthesized audio)
        Console.WriteLine("Recognizing speech...");
        await foreach (var transcript in speechService.RecognizeAsync(ToAsyncEnumerable(audioFrames), cancellationToken))
        {
            Console.WriteLine($"{(transcript.IsFinal ? "FINAL" : "INTERIM")}: {transcript.Text}");
        }
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            yield return item;
            await Task.Yield();
        }
    }
}
