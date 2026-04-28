using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.LiveVoice.Media.Audio;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.Realtime;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Agents.AI.RealtimeVoice.Azure.Transports;
using Agents.AI.RealtimeVoice.Azure.VoiceAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.RealtimeVoice.Azure.Calling;

/// <summary>
/// Creates <see cref="RealtimeVoiceAgentTransport"/> for Tier 0 (full OpenAI Realtime voice).
/// </summary>
public sealed class RealtimeVoiceTransportFactory : IAgentTransportFactory
{
    public AgentTier Tier => AgentTier.RealtimeVoice;

    public async ValueTask<AgentTransportResult> CreateAsync(
        string sessionId,
        IServiceProvider sessionServices,
        RealtimeIvrWorkflowDefinition workflow,
        CancellationToken cancellationToken = default)
    {
        var agent = sessionServices.GetRequiredService<AuthorizingRealtimeAIAgent>();
        var loggerFactory = sessionServices.GetService<ILoggerFactory>();
        var presenceDetector = sessionServices.GetService<PresenceDetectorService>();

        var thread = await agent.CreateRealtimeSessionAsync(cancellationToken: cancellationToken);

        var transport = new RealtimeVoiceAgentTransport(
            agent,
            thread,
            workflow,
            presenceDetector: presenceDetector,
            loggerFactory: loggerFactory);

        return new AgentTransportResult
        {
            Transport = transport,
            Tier = AgentTier.RealtimeVoice
        };
    }
}

/// <summary>
/// Creates <see cref="SttTtsAgentTransport"/> for Tier 1 (standard chat completion + STT/TTS).
/// Resolves the <see cref="IChatClient"/> using the configured service key (e.g., "gpt-4o").
/// </summary>
public sealed class SttTtsChatTransportFactory : IAgentTransportFactory
{
    private readonly string? _chatClientServiceKey;

    public SttTtsChatTransportFactory(string? chatClientServiceKey = null)
    {
        _chatClientServiceKey = chatClientServiceKey;
    }

    public AgentTier Tier => AgentTier.ChatCompletionTts;

    public async ValueTask<AgentTransportResult> CreateAsync(
        string sessionId,
        IServiceProvider sessionServices,
        RealtimeIvrWorkflowDefinition workflow,
        CancellationToken cancellationToken = default)
    {
        var chatClient = _chatClientServiceKey is not null
            ? sessionServices.GetRequiredKeyedService<IChatClient>(_chatClientServiceKey)
            : sessionServices.GetRequiredService<IChatClient>();

        var agent = new ChatClientAgent(chatClient);
        var session = await agent.CreateSessionAsync(cancellationToken);
        var recognizer = sessionServices.GetRequiredService<ISpeechRecognizer>();
        var synthesizer = sessionServices.GetRequiredService<ISpeechSynthesizer>();
        var loggerFactory = sessionServices.GetService<ILoggerFactory>();
        var presenceDetector = sessionServices.GetService<PresenceDetectorService>();

        var transport = new SttTtsAgentTransport(
            agent,
            session,
            recognizer,
            synthesizer,
            presenceDetector: presenceDetector,
            loggerFactory: loggerFactory);

        return new AgentTransportResult
        {
            Transport = transport,
            Tier = AgentTier.ChatCompletionTts
        };
    }
}

/// <summary>
/// Creates <see cref="SttTtsAgentTransport"/> for Tier 2 (small language model + STT/TTS).
/// Resolves the <see cref="IChatClient"/> using a keyed service (e.g., "phi-4").
/// </summary>
public sealed class SttTtsSlmTransportFactory : IAgentTransportFactory
{
    private readonly string _chatClientServiceKey;

    public SttTtsSlmTransportFactory(string chatClientServiceKey = "phi-4")
    {
        _chatClientServiceKey = chatClientServiceKey;
    }

    public AgentTier Tier => AgentTier.SmallLanguageModel;

    public async ValueTask<AgentTransportResult> CreateAsync(
        string sessionId,
        IServiceProvider sessionServices,
        RealtimeIvrWorkflowDefinition workflow,
        CancellationToken cancellationToken = default)
    {
        var chatClient = sessionServices.GetRequiredKeyedService<IChatClient>(_chatClientServiceKey);
        var agent = new ChatClientAgent(chatClient);
        var session = await agent.CreateSessionAsync(cancellationToken);
        var recognizer = sessionServices.GetRequiredService<ISpeechRecognizer>();
        var synthesizer = sessionServices.GetRequiredService<ISpeechSynthesizer>();
        var loggerFactory = sessionServices.GetService<ILoggerFactory>();
        var presenceDetector = sessionServices.GetService<PresenceDetectorService>();

        var transport = new SttTtsAgentTransport(
            agent,
            session,
            recognizer,
            synthesizer,
            presenceDetector: presenceDetector,
            loggerFactory: loggerFactory);

        return new AgentTransportResult
        {
            Transport = transport,
            Tier = AgentTier.SmallLanguageModel
        };
    }
}

/// <summary>
/// Creates <see cref="NluIntentTransport"/> for Tier 3 (NLU intent classification + STT/TTS).
/// </summary>
public sealed class NluTransportFactory : IAgentTransportFactory
{
    public AgentTier Tier => AgentTier.IntentNlu;

    public ValueTask<AgentTransportResult> CreateAsync(
        string sessionId,
        IServiceProvider sessionServices,
        RealtimeIvrWorkflowDefinition workflow,
        CancellationToken cancellationToken = default)
    {
        var classifier = sessionServices.GetRequiredService<IIntentClassifier>();
        var recognizer = sessionServices.GetRequiredService<ISpeechRecognizer>();
        var synthesizer = sessionServices.GetRequiredService<ISpeechSynthesizer>();
        var loggerFactory = sessionServices.GetService<ILoggerFactory>();
        var presenceDetector = sessionServices.GetService<PresenceDetectorService>();
        var workflowState = new IvrWorkflowState { Status = IvrWorkflowStatus.Running };

        var transport = new NluIntentTransport(
            classifier,
            workflow,
            workflowState,
            recognizer,
            synthesizer,
            presenceDetector: presenceDetector,
            loggerFactory: loggerFactory);

        return new ValueTask<AgentTransportResult>(new AgentTransportResult
        {
            Transport = transport,
            WorkflowState = workflowState,
            Tier = AgentTier.IntentNlu
        });
    }
}

/// <summary>
/// Creates <see cref="DtmfIvrTransport"/> for Tier 4 (pure DTMF menu navigation).
/// </summary>
public sealed class DtmfTransportFactory : IAgentTransportFactory
{
    public AgentTier Tier => AgentTier.DtmfOnly;

    public ValueTask<AgentTransportResult> CreateAsync(
        string sessionId,
        IServiceProvider sessionServices,
        RealtimeIvrWorkflowDefinition workflow,
        CancellationToken cancellationToken = default)
    {
        var synthesizer = sessionServices.GetRequiredService<ISpeechSynthesizer>();
        var loggerFactory = sessionServices.GetService<ILoggerFactory>();
        var presenceDetector = sessionServices.GetService<PresenceDetectorService>();
        var workflowState = new IvrWorkflowState { Status = IvrWorkflowStatus.Running };

        var transport = new DtmfIvrTransport(
            workflow,
            workflowState,
            synthesizer,
            presenceDetector: presenceDetector,
            loggerFactory: loggerFactory);

        return new ValueTask<AgentTransportResult>(new AgentTransportResult
        {
            Transport = transport,
            WorkflowState = workflowState,
            Tier = AgentTier.DtmfOnly
        });
    }
}
