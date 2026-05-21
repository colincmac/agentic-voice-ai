using System.Collections.Generic;

namespace Agents.AI.ContactCenter.Media.Analysis;

/// <summary>
/// Configuration for <see cref="ChatClientIntentClassifier"/>. All values are optional;
/// the defaults are tuned for small instruct-style models such as
/// <c>phi-4-mini-instruct</c> served via Azure Foundry chat completions.
/// </summary>
public sealed class ChatClientIntentClassifierOptions
{
    /// <summary>
    /// System prompt prepended to every classification call. The classifier appends
    /// the valid-intent list and any per-intent examples after this prompt.
    /// </summary>
    public string SystemPrompt { get; set; } =
        "You are an intent classifier for a contact-center IVR. " +
        "Given a caller utterance and a closed set of candidate intents, " +
        "respond with a single JSON object — no prose, no markdown, no code fences — " +
        "with the schema: " +
        "{\"intent\": <one of the candidates or \"none\">, " +
        "\"confidence\": <number between 0 and 1>, " +
        "\"entities\": <flat object of string → string, omit if empty>}. " +
        "If the utterance does not clearly match any candidate, return intent=\"none\".";

    /// <summary>
    /// Optional per-intent example utterances appended as few-shot context. The
    /// classifier folds these in alongside the candidate intent list. Keys must
    /// match candidate intent names; unknown keys are ignored at classify time.
    /// </summary>
    public IDictionary<string, IReadOnlyList<string>> IntentExamples { get; set; }
        = new Dictionary<string, IReadOnlyList<string>>(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Minimum confidence required to return an intent. Classifications below this
    /// threshold are coerced to <see cref="IntentResult.None"/>. Defaults to 0 (no floor).
    /// </summary>
    public double MinimumConfidence { get; set; }

    /// <summary>
    /// Temperature passed to the chat client. Lower values yield more deterministic
    /// routing decisions. Defaults to 0.
    /// </summary>
    public float? Temperature { get; set; } = 0f;

    /// <summary>
    /// Max output tokens passed to the chat client. Defaults to 128 — enough for the
    /// JSON envelope plus a handful of small entities.
    /// </summary>
    public int? MaxOutputTokens { get; set; } = 128;

    /// <summary>
    /// Language tag forwarded into the system prompt to give the model a hint
    /// when callers speak in non-English locales. Defaults to <c>en-US</c>.
    /// </summary>
    public string Language { get; set; } = "en-US";

    /// <summary>
    /// When true, asks the underlying chat client to respond in JSON via
    /// <see cref="Microsoft.Extensions.AI.ChatResponseFormat.Json"/>. Some backends
    /// (notably phi-4-mini hosted via Foundry chat completions) ignore the hint;
    /// the classifier still parses the response leniently regardless. Defaults to true.
    /// </summary>
    public bool RequestJsonResponseFormat { get; set; } = true;
}
