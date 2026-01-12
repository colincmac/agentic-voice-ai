// See https://aka.ms/new-console-template for more information
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Agents.AI.Extensions.Helpers.Streaming;
using Agents.AI.Extensions.LiveVoice;
using Agents.AI.RealtimeVoice;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Extensions.AI.Contents;
using Extensions.AI.RealtimeVoice;
using Extensions.AI.RealtimeVoice.Configuration;
using Extensions.AI.RealtimeVoice.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Models;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Showcase.ConsolePlayground;

Console.WriteLine("Hello, World!");





var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
    .Build();
var aiConfig = configuration.GetRequiredSection(AzureOpenAISettings.SectionName).Get<AzureOpenAISettings>();

if(aiConfig == null)
{
    throw new InvalidOperationException($"Configuration section '{AzureOpenAISettings.SectionName}' is missing or invalid.");
}

const string SourceName = "OpenTelemetryAspire.ConsoleApp";
const string ServiceName = "AgentOpenTelemetry";
var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") ?? "http://localhost:4318";
var activities = new List<Activity>();
var metrics = new List<Metric>();
var logs = new List<LogRecord>();

var resource = ResourceBuilder.CreateDefault()
    .AddService(ServiceName, serviceVersion: "1.0.0")
    .AddAttributes(new Dictionary<string, object>
    {
        ["service.instance.id"] = Environment.MachineName,
        ["deployment.environment"] = "development"
    });
// Setup tracing with resource
var tracerProviderBuilder = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resource)
    .AddSource(SourceName) // Our custom activity source
    .AddSource("*Extensions.AI*")
    .AddSource("*Agents.AI*")
    .AddSource("*Microsoft.Agents.AI")
    .AddConsoleExporter(); // Agent Framework telemetry
    //.AddInMemoryExporter(activities)
    //            .AddOtlpExporter(opt =>
    //            {
    //                opt.Endpoint = new Uri("https://ms-demo-prom-tl8j.westus2-1.metrics.ingest.monitor.azure.com/dataCollectionRules/dcr-793475270e1b4f818d0637d196991571/streams/Microsoft-PrometheusMetrics/api/v1/write?api-version=2023-04-24");
    //            })
    ////.AddAzureMonitorTraceExporter(opt => opt.)
    //.AddConsoleExporter();
using var tracerProvider = tracerProviderBuilder.Build();


using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resource)
    .AddMeter(SourceName) // Our custom meter
    .AddMeter("*Microsoft.Agents.AI")
    .AddConsoleExporter()
    .Build(); 
/*    .AddInMemoryExporter(metrics)*/
    //.AddConsoleExporter()
    //        .AddOtlpExporter(opt =>
    //        {
    //            opt.Endpoint = new Uri("https://ms-demo-prom-tl8j.westus2-1.metrics.ingest.monitor.azure.com/dataCollectionRules/dcr-793475270e1b4f818d0637d196991571/streams/Microsoft-PrometheusMetrics/api/v1/write?api-version=2023-04-24");
    //        })
    //.Build();
var serviceCollection = new ServiceCollection();
serviceCollection.AddLogging(loggingBuilder => loggingBuilder
    .SetMinimumLevel(LogLevel.Debug)
    .AddOpenTelemetry(options =>
    {
        options.SetResourceBuilder(resource);
        options.AddInMemoryExporter(logs);
        options.AddConsoleExporter();
        options.IncludeScopes = true;
        options.IncludeFormattedMessage = true;

    })).AddMetrics();
var serviceProvider = serviceCollection.BuildServiceProvider();
var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

if (aiConfig.UseOpenTelemetry)
{

}
using var cts = new CancellationTokenSource();
//Console.CancelKeyPress += (sender, e) =>
//{
//    // We'll stop the process manually by using the CancellationToken
//    e.Cancel = true;

//    // Change the state of the CancellationToken to "Canceled"
//    // - Set the IsCancellationRequested property to true
//    // - Call the registered callbacks
//    cts.Cancel();
//};
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
await StartBufferedAsync(cts.Token);
//await StartChatAsync(cts.Token);
#pragma warning disable CS8321 // Local function is declared but never used
//async Task StartChatAsync(CancellationToken cancellationToken)
//{
//    using var activitySource = new ActivitySource(SourceName);
//    using var meter = new Meter(SourceName);
//    var client = new AzureOpenAIClient(endpoint: new Uri(aiConfig.Endpoint), new System.ClientModel.ApiKeyCredential(aiConfig.Key))
//        .GetChatClient(aiConfig.ChatDeploymentName)
//        .AsIChatClient()
//        .AsBuilder()
//        .UseFunctionInvocation()
//        .UseOpenTelemetry(loggerFactory, SourceName)
//        .Build();
//    var chatCompletionsOptions = new ChatOptions()
//    {
//        Tools = [AIFunctionFactory.Create(GetWeatherAsync)]
//    };
//    List<ChatMessage> messages = [new ChatMessage(ChatRole.User, "What's the weather like in Seattle?")];
//    await foreach(var response in client.GetStreamingResponseAsync(messages, chatCompletionsOptions, cancellationToken))
//    {
//        Console.WriteLine(JsonSerializer.Serialize(response));
//    }
//    ;
//}

async ValueTask<object?> FunctionCallMiddleware(AIAgent agent, FunctionInvocationContext context, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next, CancellationToken cancellationToken)
{
    Console.WriteLine($"Function Name: {context!.Function.Name} - Middleware 1 Pre-Invoke");
    var result = await next(context, cancellationToken);
    Console.WriteLine($"Function Name: {context!.Function.Name} - Middleware 1 Post-Invoke");

    return result;
}
async Task StartBufferedAsync(CancellationToken cancellationToken)
{
    using var activitySource = new ActivitySource(SourceName);
    using var meter = new Meter(SourceName);
    var client = new AzureOpenAIClient(endpoint: new Uri(aiConfig.Endpoint), new System.ClientModel.ApiKeyCredential(aiConfig.Key));
    var speakerOutputSink = new SpeakerOutputSink();
    var realtimeClient = client.GetRealtimeClient();
    var voiceClient = new OpenAIRealtimeConversationClient(realtimeClient, aiConfig.RealtimeDeploymentName, loggerFactory: loggerFactory)
        .AsBuilder()
        .UseOpenTelemetry(loggerFactory, SourceName)
        .UseFunctionInvocation(loggerFactory).Build(serviceProvider);

    var agent = new RealtimeAIAgent(voiceClient, new()
    {
        Name = "VoiceAssistantAgent",
        Description = "A helpful voice assistant.",
        Instructions = "You are a helpful voice assistant.",

        ChatMessageStoreFactory = (_) => new Microsoft.Agents.AI.InMemoryChatMessageStore(),
        SessionOptions = new LiveConversationSessionOptions()
        {
            Tools = [AIFunctionFactory.Create(GetWeatherAsync)],
            ToolMode = ChatToolMode.Auto,
            Voice = "shimmer",
            InputTranscription = new RealtimeTranscriptionOptions()
            {
                Language = "en-US",
                Model = "whisper-1",
            },
            TurnDetection = new RealtimeTurnDetection()
            {
                EnableAutomaticResponse = false,
                Type = RealtimeTurnDetectionType.ServerVad,
            },
            Modalities = null,
        }

    }, loggerFactory);
    //.AsBuilder()
    //.UseOpenTelemetry(SourceName, null)
    //.Build(serviceProvider);

    //var agentInputService = agent.GetService<RealtimeAIAgent>() ?? throw new InvalidOperationException("");
    var agentThread = await agent.GetNewThreadAsync(cancellationToken);

    var defaultLogger = loggerFactory.CreateLogger<Program>();
    var testParticipantLogger = loggerFactory.CreateLogger<TestCallParticipant>();

    //var alice = new RealtimeAIAgentChannel(agent, agentThread, null);
    //var bob = new TestCallParticipant("Local", null, testParticipantLogger);

    //var session = new CallSession(loggerFactory.CreateLogger<CallSession>());
    //await session.StartAsync([alice, bob], cancellationToken);
    //var aliceToBobTask = RouteAudioAsync(alice, bob, "Alice->Bob", defaultLogger, cancellationToken);

    //var bobToAliceTask = RouteAudioAsync(bob, alice, "Bob->Alice", defaultLogger, cancellationToken);
}

//async static Task RouteAudioAsync(
//    IAudioChannel sender,
//    IAudioChannel receiver,
//    string routeName,
//    ILogger logger,
//    CancellationToken cancellationToken)
//{
//    logger.LogDebug("Starting audio route: {RouteName}", routeName);

//    try
//    {
//        while (!cancellationToken.IsCancellationRequested)
//        {
//            await foreach (var audioChunk in sender.ReceiveAudioAsync(cancellationToken))
//            {
//                await receiver.SendAudioAsync(audioChunk, cancellationToken);
//            }
//        }
//    }
//    catch (OperationCanceledException)
//    {
//        logger.LogDebug("Audio route {RouteName} cancelled", routeName);
//    }
//    catch (Exception ex)
//    {
//        logger.LogError(ex, "Error in audio route {RouteName}", routeName);
//        throw;
//    }
//}


//async Task StartAsync(CancellationToken cancellationToken)
//{
//    using var activitySource = new ActivitySource(SourceName);
//    using var meter = new Meter(SourceName);
//    var client = new AzureOpenAIClient(endpoint: new Uri(aiConfig.Endpoint), new System.ClientModel.ApiKeyCredential(aiConfig.Key));
//    var speakerOutputSink = new SpeakerOutputSink();
//    var realtimeClient = client.GetRealtimeClient();
//    var voiceClient = new OpenAIRealtimeConversationClient(realtimeClient, aiConfig.RealtimeDeploymentName, loggerFactory: loggerFactory)
//        .AsBuilder()
//        .UseOpenTelemetry(loggerFactory, SourceName)
//        .UseFunctionInvocation(loggerFactory).Build(serviceProvider);
    
//    var agent = new RealtimeVoiceSessionAgent(voiceClient, new()
//    {
//        Name = "VoiceAssistantAgent",
//        Description = "A helpful voice assistant.",
//        Instructions = "You are a helpful voice assistant.",
       
//        ChatMessageStoreFactory = (_) => new Microsoft.Agents.AI.InMemoryChatMessageStore(),
//        DefaultSessionOptions = new LiveConversationSessionOptions()
//        {
//            Tools = [AIFunctionFactory.Create(GetWeatherAsync)],
//            ToolMode = ChatToolMode.Auto,
//            Voice = "shimmer",
//            InputTranscription = new RealtimeTranscriptionOptions()
//            {
//                Language = "en-US",
//                Model = "whisper-1",
//            },
//            TurnDetection = new RealtimeTurnDetection()
//            {
//                EnableAutomaticResponse = false,
//                Type = RealtimeTurnDetectionType.ServerVad,
//            },  
//            Modalities = null,
//        }

//    }, loggerFactory)
//    .AsBuilder()
//    .UseOpenTelemetry(SourceName, null)
//    //.UseRealtime(FunctionCallMiddleware)
//    .Build(serviceProvider);

//    var agentRunOptions = new LiveVoiceAgentRunOptions()
//    {
//        TerminationPredicate = update => false, //update.RawRepresentation is ResponseFinishedUpdate,
//        //TerminationPredicate = update => update.Contents.Any(c => c is RealtimeResponseFinishedContent), //update.RawRepresentation is ResponseFinishedUpdate,
//        //ResponseOptions = new LiveConversationResponseOptions()
//        //{
//        //    Temperature = 0.7f,
//        //}

//    };
//    var thread = agent.GetNewThread();
//    // New pooled buffer
//    await using var pooledBuffer = new MediaStreamDistributor(
//        capacity: 2 * 1024 * 1024,
//        chunkSize: 8192);
//    // Create dedicated consumer for agent input
//    var agentConsumer = pooledBuffer.CreateSubscription();


//    var micCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

//    IEnumerable<ChatMessage> messages = [];
//    var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

//    var consumeTask = Task.Run(async () =>
//    {
//        while (!micCts.IsCancellationRequested)
//        {
//            await foreach (var update in agent.RunStreamingAsync(messages: messages, thread: thread, options: agentRunOptions, cancellationToken: cts.Token))
//            {
//                if (update.Contents.Any(c => c is RealtimeVadContent vc && vc.VadEvent == VadEventType.InputSpeechStarted))
//                {
//                    await speakerOutputSink.StopAudioAsync();
//                }
//                foreach (var audio in update.Contents.OfType<DataContent>().Where(d => d.HasTopLevelMediaType("audio")))
//                {
//                    await speakerOutputSink.SendAudioAsync(audio, micCts.Token);
//                }
//            }
//        }
//    }, cts.Token);
//    var agentInputService = agent.GetService<RealtimeAIAgent>();

//    try
//    {
//        while (!micCts.IsCancellationRequested)
//        {
//            using var microphoneInput = MicrophoneAudioStream.Start();
//            // Loop until canceled or stream ends
//            while (!micCts.IsCancellationRequested)
//            {
//                int bytesRead = await microphoneInput.ReadAsync(buffer.AsMemory(0, buffer.Length), micCts.Token).ConfigureAwait(false);
//                if (bytesRead == 0)
//                {
//                    // For a live microphone this typically means a transient gap; short delay prevents tight spin.
//                    await Task.Delay(10, micCts.Token).ConfigureAwait(false);
//                    continue;
//                }
//                if (agentInputService is not null)
//                {
//                    await agentInputService.SendAudioToRunAsync(new DataContent(buffer.AsMemory(0, bytesRead), "audio/pcm"), thread, cancellationToken: micCts.Token);
//                }
//            }
//        }
//    }
//    catch (OperationCanceledException) { }
//    finally
//    {
//        ArrayPool<byte>.Shared.Return(buffer);
//        micCts.Cancel();
//        try { await consumeTask.ConfigureAwait(false); } catch { /* ignored */ }
//        Console.WriteLine(JsonSerializer.Serialize(logs));
//        Console.WriteLine();
//        Console.WriteLine();
//        Console.WriteLine(JsonSerializer.Serialize(metrics));
//        Console.WriteLine();
//        Console.WriteLine();
//        Console.WriteLine(JsonSerializer.Serialize(activities));
//    }

//}
[Description("Get the weather for a given location.")]
static async Task<string> GetWeatherAsync([Description("The location to get the weather for.")] string location)
{
    await Task.Delay(1000);
    return $"The weather in {location} is cloudy with a high of 15°C.";
}

public class SpeakerOutputSink : IAudioListener
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

//public class TranscriptContextProvider : Microsoft.Agents.AI.AIContextProvider
//{
//}
