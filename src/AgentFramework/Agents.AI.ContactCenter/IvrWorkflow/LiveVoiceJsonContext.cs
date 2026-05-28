using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Agents.AI.Extensions.Helpers.Streaming;
using Microsoft.Agents.AI;

namespace Agents.AI.ContactCenter.IvrWorkflow;

public partial class LiveVoiceJsonUtilities
{
    public static JsonSerializerOptions DefaultOptions { get; } = CreateDefaultOptions();

    private static JsonSerializerOptions CreateDefaultOptions()
    {
        JsonSerializerOptions options = new()
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(AgentsAIJsonUtilities.DefaultOptions.TypeInfoResolver, LiveVoiceJsonContext.Default),
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        if (JsonSerializer.IsReflectionEnabledByDefault)
        {
            options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
            options.Converters.Add(new JsonStringEnumConverter());
        }
        options.MakeReadOnly();

        return options;
    }

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
        UseStringEnumConverter = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true)]
    [JsonSerializable(typeof(RealtimeConversationTurn))]
    public partial class LiveVoiceJsonContext : JsonSerializerContext;
}
