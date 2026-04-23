// See https://aka.ms/new-console-template for more information
#pragma warning disable CS8321 


using System.Buffers;
using Agents.AI.Hosting;
using Agents.AI.Playground.ConsoleApp;
using Agents.AI.Realtime;
using Agents.AI.RealtimeVoice;
using Azure;
using Azure.AI.VoiceLive;
using Azure.Identity;
using Extensions.AI.Contents;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using static Extensions.AI.OpenTelemetry.SemanticConventions.GenAI;

Console.WriteLine("Hello, World!");


/**
 * Configuration
 */
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.SetBasePath(Directory.GetCurrentDirectory())
    .AddEnvironmentVariables()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);
const string ServiceName = "AgentOpenTelemetry";

var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
    .Build();
var serviceCollection = new ServiceCollection();


var resource = ResourceBuilder.CreateDefault()
    .AddService(ServiceName, serviceVersion: "1.0.0")
    .AddAttributes(new Dictionary<string, object>
    {
        ["service.instance.id"] = Environment.MachineName,
        ["deployment.environment"] = "development"
    });

builder.Services.AddLogging(loggingBuilder => loggingBuilder
    .SetMinimumLevel(LogLevel.Debug)
    .AddOpenTelemetry(options =>
    {
        options.SetResourceBuilder(resource);
        options.AddConsoleExporter();
        options.IncludeScopes = true;
        options.IncludeFormattedMessage = true;

    })).AddMetrics();
builder.AddKeyedConversationClient("voicelive")
    .UseFunctionInvocation()
    .UseOpenTelemetry(sourceName: "Showcase.VoiceAgent");


builder.AddRealtimeAIAgent(
    name: "TriageAgent",
    configurationSection: builder.Configuration.GetSection("TriageAgent"),
    liveConversationClientKey: "voicelive");
builder.Services.AddHttpLogging();
var app = builder.Build();
app.UseHttpLogging();
/**
 * Application code
 */
var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
using var cts = new CancellationTokenSource();
_ = Task.Run(() =>
{
    try
    {
        Console.WriteLine("Press ESC or 'q' to quit.");
        while (!cts.IsCancellationRequested)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape || (key.Key == ConsoleKey.Q && key.Modifiers == 0))
            {
                Console.WriteLine("Cancellation requested via key press.");
                cts.Cancel();
                break;
            }
        }
    }
    catch (InvalidOperationException)
    {
        // Console input might be redirected; ignore.
    }
});

await StartAsync(cts.Token);
//await RunVoiceAssistantAsync();
async Task StartAsync(CancellationToken ct)
{
    var agent = app.Services.GetRequiredKeyedService<RealtimeAIAgent>("TriageAgent");

    var speakerOutputSink = new SpeakerOutputSink();
    using var microphoneInput = MicrophoneAudioStream.Start();

    loggerFactory.CreateLogger("Playground").LogInformation("Starting voice live conversation...");
    var thread = await agent.CreateRealtimeSessionAsync(null, ct);
    IEnumerable<ChatMessage> messages = [];
    RealtimeAgentRunOptions runOptions = new RealtimeAgentRunOptions()
    {
        InitiateConversation = true
    };
    var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

    var consumeTask = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            await foreach (var update in agent.RunStreamingAsync(messages: messages, session: thread, options: runOptions, cancellationToken: cts.Token))
            {
                if (update.Contents.Any(c => c is RealtimeVadContent vc && vc.VadEvent == VadEventType.InputSpeechStarted))
                {
                    await speakerOutputSink.StopAudioAsync();
                }
                foreach (var audio in update.Contents.OfType<DataContent>().Where(d => d.HasTopLevelMediaType("audio")))
                {
                    await speakerOutputSink.SendAudioAsync(audio, ct);
                }
            }
        }
    }, ct);

    try
    {
        while (!ct.IsCancellationRequested)
        {

            var bytesRead = await microphoneInput.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                // For a live microphone this typically means a transient gap; short delay prevents tight spin.
                await Task.Delay(10, ct).ConfigureAwait(false);
                continue;
            }
            await agent.SendAudioAsync(thread, new DataContent(buffer.AsMemory(0, bytesRead), "audio/pcm"), cancellationToken: ct);

        }

    }
    catch (OperationCanceledException) { }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer);
        try { await consumeTask.ConfigureAwait(false); } catch { /* ignored */ }
    }
}

async Task RunVoiceAssistantAsync(
    string? instructions = default,
    bool useTokenCredential = true,
    bool verbose = true)
{
    // Setup configuration
    var configuration = new ConfigurationBuilder()
        .AddJsonFile("appsettings.Development.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    // Override with command line values if provided
    var settings = configuration.GetRequiredSection("VoiceLive").Get<VoiceSettings>() ?? throw new InvalidOperationException();
    instructions = settings.Instructions ?? instructions ?? string.Empty;

    // Setup logging
    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.AddConfiguration(configuration);
        builder.AddConsole();
        if (verbose)
        {
            builder.SetMinimumLevel(LogLevel.Debug);
        }
        else
        {
            builder.SetMinimumLevel(LogLevel.Information);
        }
    });

    var logger = loggerFactory.CreateLogger<Program>();

    // Check audio system before starting
    if (!CheckAudioSystem(logger))
    {
        return;
    }

    try
    {
        VoiceLiveClientOptions options = new VoiceLiveClientOptions(VoiceLiveClientOptions.ServiceVersion.V2025_10_01);
        //AzureKeyCredential credential = new AzureKeyCredential("your-api-key");
        var credential = new VisualStudioCredential();
        var client = new VoiceLiveClient(new Uri(settings.Endpoint), credential, options);
        logger.LogInformation("Using Azure token credential");

        // Create and start voice assistant
        using var assistant = new BasicVoiceAssistant(
            client,
            settings.Model,
            settings.Voice,
            instructions,
            loggerFactory);

        // Setup cancellation token for graceful shutdown
        using var cancellationTokenSource = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            logger.LogInformation("Received shutdown signal");
            cancellationTokenSource.Cancel();
        };

        // Start the assistant
        await assistant.StartAsync(cancellationTokenSource.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n👋 Voice assistant shut down. Goodbye!");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Fatal error");
        Console.WriteLine($"❌ Error: {ex.Message}");
    }
}

static bool CheckAudioSystem(ILogger logger)
{
    try
    {
        // Try input (default device)
        using (var waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(24000, 16, 1),
            BufferMilliseconds = 50
        })
        {
            // Start/Stop to force initialization and surface any device errors
            waveIn.DataAvailable += (_, __) => { };
            waveIn.StartRecording();
            waveIn.StopRecording();
        }

        // Try output (default device)
        var buffer = new BufferedWaveProvider(new WaveFormat(24000, 16, 1))
        {
            BufferDuration = TimeSpan.FromMilliseconds(200)
        };

        using (var waveOut = new WaveOutEvent { DesiredLatency = 100 })
        {
            waveOut.Init(buffer);
            // Playing isn't strictly required to validate a device, but it's safe
            waveOut.Play();
            waveOut.Stop();
        }

        logger.LogInformation("Audio system check passed (default input/output initialized).");
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Audio system check failed: {ex.Message}");
        return false;
    }
}
    

public class SpeakerOutputSink
{
    SpeakerOutput _speakerOutput = new();

    public Task SendAudioAsync(DataContent audioEvent, CancellationToken cancellationToken = default)
    {
        _speakerOutput.EnqueueForPlayback(audioEvent);
        return Task.CompletedTask;
    }

    public Task StopAudioAsync(CancellationToken cancellationToken = default)
    {
        _speakerOutput.ClearPlayback();
        return Task.CompletedTask;
    }
}

public record VoiceSettings(
    string Endpoint,
    string Model,
    string Voice,
    string? Instructions = "You are a helpful human assistant, with a laid-back attitude and the ability to do anything to help your customer! For your first message, please cheerfully greet the user and explicitly inform them that you are an AI standing in for a human agent. You respond only in English."
);
