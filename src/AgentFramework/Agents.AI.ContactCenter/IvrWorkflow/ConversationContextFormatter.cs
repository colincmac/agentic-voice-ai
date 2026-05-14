using System.Text;

namespace Agents.AI.ContactCenter.IvrWorkflow;

internal static class ConversationContextFormatter
{
    public static string? Format(ConversationContext? context)
    {
        if (context is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("## Pinned Conversation Context (structured, always current)");
        builder.AppendLine($"- caller_name: {FormatValue(context.CallerName)}");
        builder.AppendLine($"- caller_id: {FormatValue(context.CallerId)}");
        builder.AppendLine($"- auth_level: {context.AuthLevel}");
        builder.AppendLine($"- primary_intent: {FormatValue(context.PrimaryIntent)}");
        builder.AppendLine($"- secondary_intents: {FormatSequence(context.SecondaryIntents)}");
        builder.AppendLine($"- intent_confirmed: {context.IntentConfirmed}");
        builder.AppendLine($"- running_text_sentiment: {context.RunningTextSentiment:F2}");
        builder.AppendLine($"- running_audio_emotion: {context.RunningAudioEmotion:F2}");
        builder.AppendLine($"- frustration_detected: {context.FrustrationDetected}");
        builder.AppendLine($"- escalation_signal_count: {context.EscalationSignalCount}");
        builder.AppendLine($"- turn_count: {context.TurnCount}");
        builder.AppendLine($"- total_duration: {context.TotalDuration}");
        builder.AppendLine($"- avg_user_utterance_length_sec: {context.AvgUserUtteranceLengthSec:F2}");
        builder.AppendLine($"- audio_quality: {context.AudioQuality}");
        builder.AppendLine($"- estimated_signal_to_noise_ratio: {FormatNullableDouble(context.EstimatedSignalToNoiseRatio)}");
        builder.AppendLine($"- conversation_summary: {FormatValue(context.ConversationSummary)}");
        builder.AppendLine($"- actions_taken: {FormatSequence(context.ActionsTaken)}");

        return builder.ToString().TrimEnd();
    }

    private static string FormatNullableDouble(double? value)
    {
        return value is double number
            ? number.ToString("F2")
            : "(null)";
    }

    private static string FormatSequence(IEnumerable<string> values)
    {
        var items = values.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();

        return items.Length > 0
            ? string.Join(", ", items)
            : "None";
    }

    private static string FormatValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "(null)"
            : value;
    }
}
