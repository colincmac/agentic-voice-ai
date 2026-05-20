using Azure.Core;
using Azure.Identity;
using Microsoft.CognitiveServices.Speech;

namespace Agents.AI.ContactCenter.Configuration;

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

    public TokenCredential Credential { get; set; } = new DefaultAzureCredential();
}
