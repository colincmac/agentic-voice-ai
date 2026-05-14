using System.Threading.Channels;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Media.Transcription;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Showcase.Agent.VoiceAgent.Authentication;

/// <summary>
/// Demo <see cref="ISpeechRecognizer"/> that produces canned transcripts on a timer
/// instead of running STT. Lets the showcase exercise <c>NluConversationStrategy</c>
/// without a Cognitive Services dependency.
/// </summary>
/// <remarks>
/// Audio frames written via <see cref="WriteAudioAsync"/> are discarded. The recognizer
/// emits each entry of <see cref="ScriptedUtterances"/> as a final
/// <see cref="TranscriptSegment"/> spaced <see cref="UtteranceInterval"/> apart, then
/// idles. Replace with an Azure Speech / OpenAI Whisper / SpeechBrain implementation in
/// production.
/// </remarks>
public sealed class StubSpeechRecognizer : ISpeechRecognizer
{
    private readonly Channel<TranscriptSegment> _segments;
    private readonly ILogger<StubSpeechRecognizer> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _emitterLoop;
    private int _started;

    public StubSpeechRecognizer(ILogger<StubSpeechRecognizer>? logger = null)
    {
        _logger = logger ?? NullLogger<StubSpeechRecognizer>.Instance;
        _segments = Channel.CreateUnbounded<TranscriptSegment>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    }

    /// <summary>The list of utterances the stub will emit, in order.</summary>
    public IReadOnlyList<string> ScriptedUtterances { get; init; } =
    [
        "I want to talk to an agent."
    ];

    /// <summary>Time between scripted utterances.</summary>
    public TimeSpan UtteranceInterval { get; init; } = TimeSpan.FromSeconds(8);

    public Task WriteAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        // First write kicks off the emitter — gives the strategy time to attach before transcripts arrive.
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
        {
            _emitterLoop = Task.Run(EmitAsync, CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<TranscriptSegment> GetTranscriptsAsync(CancellationToken cancellationToken = default)
        => _segments.Reader.ReadAllAsync(cancellationToken);

    public Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        _segments.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_emitterLoop is not null)
        {
            try { await _emitterLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        _segments.Writer.TryComplete();
        _cts.Dispose();
    }

    private async Task EmitAsync()
    {
        try
        {
            // Initial pause so the strategy has spoken its prompt before we "respond".
            await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token).ConfigureAwait(false);

            foreach (var utterance in ScriptedUtterances)
            {
                _logger.LogInformation("Stub recognizer emitting transcript: {Utterance}", utterance);
                await _segments.Writer.WriteAsync(new TranscriptSegment
                {
                    Text = utterance,
                    Role = ChatRole.User,
                    IsFinal = true,
                    UtteranceStart = DateTimeOffset.UtcNow,
                    UtteranceEnd = DateTimeOffset.UtcNow,
                    Confidence = 0.9
                }, _cts.Token).ConfigureAwait(false);

                await Task.Delay(UtteranceInterval, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stub recognizer emitter terminated");
        }
    }
}
