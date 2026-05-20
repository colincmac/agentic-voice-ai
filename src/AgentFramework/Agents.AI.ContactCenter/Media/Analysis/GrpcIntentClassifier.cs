using Agents.AI.ContactCenter.Calling;
using Agents.Intent.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Media.Analysis;

/// <summary>
/// <see cref="IIntentClassifier"/> adapter that delegates classification to
/// the <c>intent-agent</c> gRPC service over the cluster network. This is
/// the voice-edge side of the two-pool AKS topology — see
/// <c>docs/architecture/aks-topology.md</c> for the deployment view and
/// scaling shape.
/// </summary>
/// <remarks>
/// Each call opens a single bidirectional stream, sends one final
/// <see cref="Utterance"/>, awaits the matching <see cref="IntentScore"/>,
/// and closes the stream. The bidirectional shape is preserved on the wire
/// so a future swap-in (partial-utterance scoring against a real SLM) does
/// not require any client change.
/// </remarks>
public sealed class GrpcIntentClassifier : IIntentClassifier
{
    private readonly IntentClassification.IntentClassificationClient _client;
    private readonly ILogger<GrpcIntentClassifier> _logger;

    public GrpcIntentClassifier(
        IntentClassification.IntentClassificationClient client,
        ILogger<GrpcIntentClassifier> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async ValueTask<IntentResult> ClassifyAsync(
        string utterance,
        IReadOnlyList<string> validIntents,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(utterance) || validIntents.Count == 0)
        {
            return IntentResult.None;
        }

        try
        {
            using var call = _client.Classify(cancellationToken: cancellationToken);

            var request = new Utterance
            {
                Text = utterance,
                Language = "en-US",
                IsFinal = true,
            };

            for (var i = 0; i < validIntents.Count; i++)
            {
                request.ValidIntents.Add(validIntents[i]);
            }

            await call.RequestStream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            await call.RequestStream.CompleteAsync().ConfigureAwait(false);

            IntentScore? finalScore = null;
            await foreach (var score in call.ResponseStream.ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (score.IsFinal)
                {
                    finalScore = score;
                }
            }

            if (finalScore is null || string.IsNullOrEmpty(finalScore.IntentName))
            {
                return IntentResult.None;
            }

            return new IntentResult
            {
                IntentName = finalScore.IntentName,
                Confidence = finalScore.Confidence,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex,
                "Intent-agent gRPC call failed ({Status}); returning IntentResult.None",
                ex.StatusCode);
            return IntentResult.None;
        }
    }
}
