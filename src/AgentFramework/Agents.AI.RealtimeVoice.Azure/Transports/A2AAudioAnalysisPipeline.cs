using Agents.AI.Extensions.LiveVoice.Media.Analysis;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.A2A;
using Microsoft.Extensions.AI;

namespace Agents.AI.RealtimeVoice.Azure.Transports;

/// <summary>
/// <see cref="IAudioAnalysisPipeline"/> implementation that delegates audio
/// analysis to a remote A2A agent. Encodes audio as base64 <see cref="DataContent"/>
/// in the message, and expects the agent to return structured
/// <see cref="AudioAnalysisResult"/> in its response.
/// <para>
/// This enables running the audio analysis model on a separate service/GPU
/// while keeping the transport architecture uniform.
/// </para>
/// </summary>
public sealed class A2AAudioAnalysisPipeline : IAudioAnalysisPipeline
{
    private readonly AIAgent _agent;
    private readonly A2AAgentSession _thread;

    public A2AAudioAnalysisPipeline(AIAgent agent, A2AAgentSession agentSession)
    {
        _agent = agent;
        _thread = agentSession;
    }

    public async Task<AudioAnalysisResult?> AnalyzeAsync(
        ReadOnlyMemory<byte> audioWindow,
        int sampleRateHz = 16_000,
        CancellationToken cancellationToken = default)
    {
        var message = new ChatMessage(ChatRole.User,
        [
            new DataContent(audioWindow, $"audio/pcm;rate={sampleRateHz}"),
            new TextContent($"Analyze this {audioWindow.Length / (sampleRateHz * 2.0):F1}s audio window. " +
                            "Return emotion label, valence (-1 to +1), confidence, speech rate, and stress level.")
        ]);

        var response = await _agent.RunAsync(
            message,
            _thread,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Parse the A2A agent's structured response
        // Implementation depends on the agent's output format
        return ParseResponse(response);
    }

    private static AudioAnalysisResult? ParseResponse(AgentResponse response)
    {
        // The A2A agent should return structured data;
        // parse from the response content based on your agent's contract
        var text = response.Messages
            .SelectMany(m => m.Contents.OfType<TextContent>())
            .FirstOrDefault()?.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // TODO: Deserialize from the agent's structured output format
        // For now, return a placeholder indicating the A2A path is wired
        return new AudioAnalysisResult
        {
            Confidence = 0.0
        };
    }
}
