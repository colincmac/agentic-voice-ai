using System.Text;
using Microsoft.Extensions.AI;


namespace Extensions.AI.Contents;

public static class RealtimeAIContentExtensions
{
    /// <summary>Concatenates the text of all <see cref="TextContent"/> instances in the list.</summary>
    public static string ConcatTranscript(this IEnumerable<AudioTranscriptionContent> contents)
    {
        if (contents is IList<AIContent> list)
        {
            int count = list.Count;
            switch (count)
            {
                case 0:
                    return string.Empty;

                case 1:
                    return (list[0] as AudioTranscriptionContent)?.Text ?? string.Empty;

                default:
                    StringBuilder builder = new();
                    for (int i = 0; i < count; i++)
                    {
                        if (list[i] is AudioTranscriptionContent text)
                        {
                            builder.Append(text.Text);
                        }
                    }

                    return builder.ToString();
            }
        }

        return string.Concat(contents.OfType<AudioTranscriptionContent>());
    }

    /// <summary>Concatenates the <see cref="ChatMessage.Text"/> of all <see cref="ChatMessage"/> instances in the list.</summary>
    /// <remarks>A newline separator is added between each non-empty piece of text.</remarks>
    public static string ConcatText(this IList<ChatMessage> messages)
    {
        int count = messages.Count;
        switch (count)
        {
            case 0:
                return string.Empty;

            case 1:
                return messages[0].Text;

            default:
                StringBuilder builder = new();
                for (int i = 0; i < count; i++)
                {
                    string text = messages[i].Text;
                    if (text.Length > 0)
                    {
                        if (builder.Length > 0)
                        {
                            builder.AppendLine();
                        }

                        builder.Append(text);
                    }
                }

                return builder.ToString();
        }
    }
}
