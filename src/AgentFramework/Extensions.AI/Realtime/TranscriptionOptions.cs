using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.Extensions.AI;

public class TranscriptionOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TranscriptionOptions"/> class.
    /// </summary>
    public TranscriptionOptions()
    {
    }

    /// <summary>
    /// Gets or sets the language of the input speech audio.
    /// </summary>
    /// <remarks>
    /// The language should be specified in ISO-639-1 format (e.g. "en").
    /// Supplying the input speech language improves transcription accuracy and latency.
    /// </remarks>
    public string? SpeechLanguage { get; set; }

    /// <summary>
    /// Gets or sets the model ID to use for transcription.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// Gets or sets an optional prompt to guide the transcription.
    /// </summary>
    public string? Prompt { get; set; }
}
