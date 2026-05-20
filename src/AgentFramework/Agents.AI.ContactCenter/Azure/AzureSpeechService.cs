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
/// Implements both <see cref="ISpeechRecognizer"/> and <see cref="ISpeechSynthesizer"/> for
/// direct use in Contact Center strategies.
/// </summary>
/// <remarks>
/// This service wraps <see cref="AzureSpeechRecognizer"/> and <see cref="AzureSpeechSynthesizer"/>
/// with shared configuration and lifecycle management. Use this when you need both STT and TTS
/// capabilities in the same application context.
/// 
/// The recognizer implementation is session-scoped: each instance manages one recognition session.
/// The synthesizer implementation is stateless and thread-safe: can be reused across multiple calls.
/// </remarks>
public sealed class AzureSpeechService : ISpeechRecognizer, ISpeechSynthesizer
{
    private readonly AzureSpeechServiceOptions _options;
    private readonly ILogger<AzureSpeechService> _logger;
    private readonly SpeechConfig _speechConfig;

    // Synthesizer backing (lazy, shared singleton)
    private AzureSpeechSynthesizer? _synthesizer;

    // Recognizer backing (created lazily per instance)
    private AzureSpeechRecognizer? _recognizer;

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

    #region ISpeechSynthesizer Implementation

    /// <inheritdoc />
    IAsyncEnumerable<ReadOnlyMemory<byte>> ISpeechSynthesizer.SynthesizeAsync(
        string text,
        SynthesizerInputFormat inputFormat,
        CancellationToken cancellationToken)
    {
        return GetSynthesizer().SynthesizeAsync(text, inputFormat, cancellationToken);
    }

    #endregion

    #region ISpeechRecognizer Implementation

    /// <inheritdoc />
    async Task ISpeechRecognizer.WriteAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        _recognizer ??= new AzureSpeechRecognizer(
            _options.Endpoint,
            locale: _options.RecognitionLocale,
            concurrency: _options.Concurrency,
            logger: _logger as ILogger<AzureSpeechRecognizer>);

        await _recognizer.WriteAudioAsync(audioData, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    IAsyncEnumerable<TranscriptSegment> ISpeechRecognizer.GetTranscriptsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        _recognizer ??= new AzureSpeechRecognizer(
            _options.Endpoint,
            locale: _options.RecognitionLocale,
            concurrency: _options.Concurrency,
            logger: _logger as ILogger<AzureSpeechRecognizer>);

        return _recognizer.GetTranscriptsAsync(cancellationToken);
    }

    /// <inheritdoc />
    async Task ISpeechRecognizer.CompleteAsync(CancellationToken cancellationToken)
    {
        if (_recognizer is not null)
        {
            await _recognizer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    #endregion

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        _logger.LogInformation("Disposing Azure Speech Service");

        // Dispose recognizer if it was created
        if (_recognizer is not null)
        {
            await _recognizer.DisposeAsync().ConfigureAwait(false);
        }

        // Dispose synthesizer if it was created
        if (_synthesizer is not null)
        {
            _synthesizer.Dispose();
        }

        // SpeechConfig doesn't implement IDisposable in the Azure Speech SDK
        // It will be garbage collected when no longer referenced
    }
}
