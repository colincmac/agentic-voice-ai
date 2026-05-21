using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Media.Analysis;

/// <summary>
/// <see cref="IIntentClassifier"/> implementation backed by a
/// <see cref="IChatClient"/>. Designed for small instruct-style models exposed via
/// chat completions — e.g. <c>phi-4-mini-instruct</c> deployed in Azure Foundry —
/// and drops straight into <c>NluConversationStrategy</c> as the Tier 3 intent
/// classifier.
/// </summary>
/// <remarks>
/// The classifier builds a tightly constrained prompt that lists the valid intent
/// names (plus optional few-shot examples from <see cref="ChatClientIntentClassifierOptions.IntentExamples"/>),
/// asks the model to respond with a single JSON object, and parses the response
/// leniently — the first JSON object in the reply wins so models that wrap output
/// in markdown fences still work. The returned intent is validated against the
/// candidate set; any unknown name (or the literal <c>"none"</c>) collapses to
/// <see cref="IntentResult.None"/>.
/// </remarks>
public sealed class ChatClientIntentClassifier : IIntentClassifier
{
    private static readonly ActivitySource activitySource =
        new("Agents.AI.ContactCenter.IntentClassification");

    private readonly IChatClient _chatClient;
    private readonly ChatClientIntentClassifierOptions _options;
    private readonly ILogger<ChatClientIntentClassifier> _logger;

    public ChatClientIntentClassifier(
        IChatClient chatClient,
        ChatClientIntentClassifierOptions? options = null,
        ILogger<ChatClientIntentClassifier>? logger = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _options = options ?? new ChatClientIntentClassifierOptions();
        _logger = logger ?? NullLogger<ChatClientIntentClassifier>.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<IntentResult> ClassifyAsync(
        string utterance,
        IReadOnlyList<string> validIntents,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(utterance) || validIntents is null || validIntents.Count == 0)
        {
            return IntentResult.None;
        }

        using var activity = activitySource.StartActivity("intent.classify.chatclient");
        activity?.SetTag("intent.candidate_count", validIntents.Count);
        activity?.SetTag("intent.utterance_length", utterance.Length);

        var messages = BuildMessages(utterance, validIntents);
        var chatOptions = BuildChatOptions();

        ChatResponse response;
        try
        {
            response = await _chatClient.GetResponseAsync(messages, chatOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogWarning(ex,
                "Chat-client intent classification failed; returning IntentResult.None");
            return IntentResult.None;
        }

        var raw = response.Text;
        if (string.IsNullOrWhiteSpace(raw))
        {
            activity?.SetTag("intent.result", "none");
            activity?.SetTag("intent.failure_reason", "empty_response");
            return IntentResult.None;
        }

        if (!TryParseJsonObject(raw, out var doc))
        {
            activity?.SetTag("intent.result", "none");
            activity?.SetTag("intent.failure_reason", "unparseable_response");
            _logger.LogDebug("Intent classifier received unparseable response: {Raw}", raw);
            return IntentResult.None;
        }

        using (doc)
        {
            var result = ProjectResult(doc.RootElement, validIntents);
            activity?.SetTag("intent.result", result.IntentName ?? "none");
            activity?.SetTag("intent.confidence", result.Confidence);
            return result;
        }
    }

    private List<ChatMessage> BuildMessages(string utterance, IReadOnlyList<string> validIntents)
    {
        var userPrompt = new StringBuilder();
        userPrompt.Append("Language: ").AppendLine(_options.Language);
        userPrompt.AppendLine("Candidate intents:");
        for (var i = 0; i < validIntents.Count; i++)
        {
            userPrompt.Append("- ").AppendLine(validIntents[i]);
        }
        userPrompt.AppendLine("- none");

        var examples = _options.IntentExamples;
        if (examples is { Count: > 0 })
        {
            var wroteHeader = false;
            for (var i = 0; i < validIntents.Count; i++)
            {
                var name = validIntents[i];
                if (!examples.TryGetValue(name, out var samples) || samples is null || samples.Count == 0)
                {
                    continue;
                }

                if (!wroteHeader)
                {
                    userPrompt.AppendLine();
                    userPrompt.AppendLine("Examples:");
                    wroteHeader = true;
                }

                foreach (var sample in samples)
                {
                    if (string.IsNullOrWhiteSpace(sample))
                    {
                        continue;
                    }
                    userPrompt.Append("- intent=").Append(name).Append(" → \"").Append(sample).Append("\"\n");
                }
            }
        }

        userPrompt.AppendLine();
        userPrompt.Append("Utterance: \"").Append(utterance).Append('"');

        return new List<ChatMessage>
        {
            new(ChatRole.System, _options.SystemPrompt),
            new(ChatRole.User, userPrompt.ToString()),
        };
    }

    private ChatOptions BuildChatOptions()
    {
        var chatOptions = new ChatOptions();
        if (_options.Temperature is { } temperature)
        {
            chatOptions.Temperature = temperature;
        }
        if (_options.MaxOutputTokens is { } maxTokens)
        {
            chatOptions.MaxOutputTokens = maxTokens;
        }
        if (_options.RequestJsonResponseFormat)
        {
            chatOptions.ResponseFormat = ChatResponseFormat.Json;
        }
        return chatOptions;
    }

    private static bool TryParseJsonObject(string raw, out JsonDocument document)
    {
        // Models often wrap JSON in markdown fences or include leading prose; scan for the
        // first '{' and parse the smallest balanced object starting there.
        var span = raw.AsSpan();
        var start = span.IndexOf('{');
        if (start < 0)
        {
            document = null!;
            return false;
        }

        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < span.Length; i++)
        {
            var c = span[i];
            if (escape)
            {
                escape = false;
                continue;
            }
            if (c == '\\' && inString)
            {
                escape = true;
                continue;
            }
            if (c == '"')
            {
                inString = !inString;
                continue;
            }
            if (inString)
            {
                continue;
            }
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    var slice = span.Slice(start, i - start + 1);
                    try
                    {
                        document = JsonDocument.Parse(slice.ToString());
                        return true;
                    }
                    catch (JsonException)
                    {
                        document = null!;
                        return false;
                    }
                }
            }
        }

        document = null!;
        return false;
    }

    private IntentResult ProjectResult(JsonElement root, IReadOnlyList<string> validIntents)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return IntentResult.None;
        }

        string? intentName = null;
        if (root.TryGetProperty("intent", out var intentElement))
        {
            intentName = intentElement.ValueKind switch
            {
                JsonValueKind.String => intentElement.GetString(),
                JsonValueKind.Null => null,
                _ => intentElement.ToString(),
            };
        }
        // Tolerate alternate keys some models gravitate toward.
        else if (root.TryGetProperty("intent_name", out var altName))
        {
            intentName = altName.GetString();
        }
        else if (root.TryGetProperty("name", out var nameElement))
        {
            intentName = nameElement.GetString();
        }

        if (string.IsNullOrWhiteSpace(intentName) ||
            string.Equals(intentName, "none", StringComparison.OrdinalIgnoreCase))
        {
            return IntentResult.None;
        }

        // Constrain to the supplied candidate set — case-insensitive match preserves the
        // canonical name spelling that the workflow knows about.
        string? canonical = null;
        for (var i = 0; i < validIntents.Count; i++)
        {
            if (string.Equals(validIntents[i], intentName, StringComparison.OrdinalIgnoreCase))
            {
                canonical = validIntents[i];
                break;
            }
        }
        if (canonical is null)
        {
            _logger.LogDebug(
                "Intent classifier returned out-of-set intent '{Intent}'; coercing to none", intentName);
            return IntentResult.None;
        }

        var confidence = 0.0;
        if (root.TryGetProperty("confidence", out var confidenceElement))
        {
            confidence = confidenceElement.ValueKind switch
            {
                JsonValueKind.Number => confidenceElement.TryGetDouble(out var d) ? d : 0.0,
                JsonValueKind.String =>
                    double.TryParse(confidenceElement.GetString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0.0,
                _ => 0.0,
            };
        }
        else
        {
            // The model omitted a confidence score; treat a clean intent name as high-confidence.
            confidence = 1.0;
        }

        if (confidence < 0.0)
        {
            confidence = 0.0;
        }
        else if (confidence > 1.0)
        {
            confidence = 1.0;
        }

        if (confidence < _options.MinimumConfidence)
        {
            return IntentResult.None;
        }

        IReadOnlyDictionary<string, string>? entities = null;
        if (root.TryGetProperty("entities", out var entitiesElement) &&
            entitiesElement.ValueKind == JsonValueKind.Object)
        {
            Dictionary<string, string>? map = null;
            foreach (var property in entitiesElement.EnumerateObject())
            {
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.ToString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => null,
                    _ => property.Value.GetRawText(),
                };
                if (value is null)
                {
                    continue;
                }
                map ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                map[property.Name] = value;
            }
            if (map is { Count: > 0 })
            {
                entities = map;
            }
        }

        return new IntentResult
        {
            IntentName = canonical,
            Confidence = confidence,
            Entities = entities,
        };
    }
}
