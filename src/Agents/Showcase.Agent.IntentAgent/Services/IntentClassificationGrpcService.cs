using Agents.AI.ContactCenter.Media.Analysis;
using Agents.Intent.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Showcase.Agent.IntentAgent.Services;

/// <summary>
/// gRPC façade exposing an <see cref="IIntentClassifier"/> to the voice-edge.
/// One bidirectional stream per utterance: client sends one or more
/// <see cref="Utterance"/> messages, server emits zero or more
/// <see cref="IntentScore"/> partials and exactly one final
/// <see cref="IntentScore"/> with <c>IsFinal=true</c>.
/// </summary>
/// <remarks>
/// The showcase build delegates to an in-process keyword classifier so the
/// service is testable end-to-end without GPU weights. A Phi-4-mini /
/// ONNX-runtime host plugs in behind the same <see cref="IIntentClassifier"/>
/// contract without any wire-protocol change. See
/// <c>docs/architecture/aks-topology.md</c> for the GPU node-pool topology
/// and KEDA scaling shape.
/// </remarks>
public sealed class IntentClassificationGrpcService : IntentClassification.IntentClassificationBase
{
    private readonly IIntentClassifier _classifier;
    private readonly ILogger<IntentClassificationGrpcService> _logger;

    public IntentClassificationGrpcService(
        IIntentClassifier classifier,
        ILogger<IntentClassificationGrpcService> logger)
    {
        _classifier = classifier;
        _logger = logger;
    }

    public override async Task Classify(
        IAsyncStreamReader<Utterance> requestStream,
        IServerStreamWriter<IntentScore> responseStream,
        ServerCallContext context)
    {
        await foreach (var utterance in requestStream.ReadAllAsync(context.CancellationToken)
            .ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(utterance.Text))
            {
                continue;
            }

            // Only the final utterance carries the authoritative classification
            // for the showcase classifier; partials are forwarded with the same
            // score so the client can choose to react early.
            var validIntents = utterance.ValidIntents.Count == 0
                ? Array.Empty<string>()
                : utterance.ValidIntents.ToArray();

            IntentResult result;
            try
            {
                result = await _classifier
                    .ClassifyAsync(utterance.Text, validIntents, context.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Intent classification failed for session {SessionId}; emitting empty result",
                    utterance.SessionId);
                result = IntentResult.None;
            }

            var score = new IntentScore
            {
                SessionId = utterance.SessionId,
                IntentName = result.IntentName ?? string.Empty,
                Confidence = result.Confidence,
                IsFinal = utterance.IsFinal,
            };

            await responseStream.WriteAsync(score, context.CancellationToken).ConfigureAwait(false);
        }
    }
}
