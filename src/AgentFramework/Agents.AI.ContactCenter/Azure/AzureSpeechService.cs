using System.Runtime.CompilerServices;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Media.Transcription;
using Azure.Identity;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Azure;

/// <summary>
/// Configuration options for Azure Speech Service.
/// </summary>
public sealed class AzureSpeechServiceOptions
{
    public const string SectionName = "AzureSpeech";

    /// <summary>Azure Speech service endpoint URI.</summary>
    public required Uri Endpoint { get; set; }

    /// <summary>Speech recognition locale (default: en-US).</summary>
    public string RecognitionLocale { get; set; } = "en-US";

    /// <summary>Speech synthesis voice name (default: en-US-Ava:DragonHDLatestNeural).</summary>
    public string SynthesisVoiceName { get; set; } = "en-US-Ava:DragonHDLatestNeural";

    /// <summary>Speech synthesis locale (default: en-US).</summary>
    public string SynthesisLocale { get; set; } = "en-US";

    /// <summary>Speech synthesis voice gender (default: Female).</summary>
    public string SynthesisGender { get; set; } = "Female";

    /// <summary>Speech synthesis output format.</summary>
    public SpeechSynthesisOutputFormat OutputFormat { get; set; } = SpeechSynthesisOutputFormat.Raw24Khz16BitMonoPcm;

    /// <summary>Number of pre-warmed recognizer/synthesizer instances (default: 2).</summary>
    public int Concurrency { get; set; } = 2;

    /// <summary>Maximum number of pooled instances to retain (default: 100).</summary>
    public int MaximumRetainedCapacity { get; set; } = 100;
}

/// <summary>
/// Composite Azure Speech service providing both speech recognition (STT) and synthesis (TTS).
/// Manages shared <see cref="SpeechConfig"/> and coordinates recognizer/synthesizer pools.
/// </summary>
/// <remarks>
/// This service wraps <see cref="AzureSpeechRecognizer"/> and <see cref="AzureSpeechSynthesizer"/>
/// with shared configuration and lifecycle management. Use this when you need both STT and TTS
/// capabilities in the same application context.
/// </remarks>
public sealed class AzureSpeechService : IAsyncDisposable
{
    private readonly AzureSpeechServiceOptions _options;
    private readonly ILogger<AzureSpeechService> _logger;
    private readonly SpeechConfig _speechConfig;

    private AzureSpeechSynthesizer? _synthesizer;
    private bool _isDisposed;

    public AzureSpeechService(
        IOptions<AzureSpeechServiceOptions> options,
        ILogger<AzureSpeechService>? logger = null)
        : this(options.Value, logger)
    {
    }

    public AzureSpeechService(
        AzureSpeechServiceOptions options,
        ILogger<AzureSpeechService>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<AzureSpeechService>.Instance;

        // Create shared SpeechConfig
        _speechConfig = SpeechConfig.FromEndpoint(options.Endpoint, new AzureCliCredential());
        _speechConfig.SpeechRecognitionLanguage = options.RecognitionLocale;
        _speechConfig.SetSpeechSynthesisOutputFormat(options.OutputFormat);

        _logger.LogInformation(
            "Azure Speech Service initialized: Endpoint={Endpoint} RecognitionLocale={RecognitionLocale} SynthesisVoice={SynthesisVoice}",
            options.Endpoint,
            options.RecognitionLocale,
            options.SynthesisVoiceName);
    }

    /// <summary>
    /// Creates a new <see cref="ISpeechRecognizer"/> instance for continuous speech-to-text recognition.
    /// The recognizer is pulled from a pre-warmed pool to minimize first-byte latency.
    /// </summary>
    /// <returns>A new speech recognizer instance. The caller must dispose it after use.</returns>
    public ISpeechRecognizer CreateRecognizer()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        _logger.LogDebug("Creating new speech recognizer");

        return new AzureSpeechRecognizer(
            _options.Endpoint,
            locale: _options.RecognitionLocale,
            concurrency: _options.Concurrency,
            logger: _logger as ILogger<AzureSpeechRecognizer>);
    }

    /// <summary>
    /// Gets the shared <see cref="ISpeechSynthesizer"/> instance for text-to-speech synthesis.
    /// The synthesizer maintains a pool of pre-warmed connections that are reused across calls.
    /// </summary>
    /// <returns>The shared synthesizer instance.</returns>
    public ISpeechSynthesizer GetSynthesizer()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_synthesizer is null)
        {
            _logger.LogDebug("Creating shared speech synthesizer");

            _synthesizer = new AzureSpeechSynthesizer(
                _options.Endpoint,
                voiceName: _options.SynthesisVoiceName,
                outputFormat: _options.OutputFormat,
                concurrency: _options.Concurrency,
                locale: _options.SynthesisLocale,
                gender: _options.SynthesisGender,
                logger: _logger as ILogger<AzureSpeechSynthesizer>);
        }

        return _synthesizer;
    }

    /// <summary>
    /// Convenience method to synthesize text directly without managing the synthesizer lifecycle.
    /// </summary>
    /// <param name="text">The text to synthesize into speech.</param>
    /// <param name="inputFormat">The input format (Text or SSML).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An async stream of raw audio frames (PCM).</returns>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        SynthesizerInputFormat inputFormat = SynthesizerInputFormat.Text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var synthesizer = GetSynthesizer();

        await foreach (var frame in synthesizer.SynthesizeAsync(text, inputFormat, cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    /// <summary>
    /// Convenience method to create a recognizer and stream transcripts from audio.
    /// </summary>
    /// <param name="audioStream">Async stream of raw PCM audio data.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An async stream of transcript segments.</returns>
    public async IAsyncEnumerable<TranscriptSegment> RecognizeAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var recognizer = CreateRecognizer();

        // Start streaming audio into the recognizer
        var audioTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var audioData in audioStream.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    await recognizer.WriteAudioAsync(audioData, cancellationToken).ConfigureAwait(false);
                }

                await recognizer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Audio streaming canceled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error streaming audio to recognizer");
            }
        }, cancellationToken);

        // Stream transcripts as they arrive
        await foreach (var segment in recognizer.GetTranscriptsAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return segment;
        }

        // Wait for audio streaming to complete
        await audioTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        _logger.LogInformation("Disposing Azure Speech Service");

        if (_synthesizer is not null)
        {
            _synthesizer.Dispose();
        }

        // SpeechConfig doesn't implement IDisposable in the Azure Speech SDK
        // It will be garbage collected when no longer referenced
        await Task.CompletedTask;
    }
}
