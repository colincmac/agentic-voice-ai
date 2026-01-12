using System.ComponentModel;
using Agents.AI.Extensions.AITools;
using Agents.AI.Extensions.ToolApproval.VoiceApproval;
using Microsoft.Extensions.AI;

namespace Showcase.Agent.VoiceAgent;

public class TestTools : IAIToolCollection
{
    [Description("Get the weather for a given location."), RequiresVoiceApproval("Ask for approval before executing this tool.")]
    static Task<string> GetWeatherAsync([Description("The location to get the weather for.")] string location)
    {
        //await Task.Delay(1000);
        return Task.FromResult($"The weather in {location} is cloudy with a high of 15°C.");
    }

    public IEnumerable<AITool> AsAITools()
    {
        yield return AIFunctionFactory.Create(GetWeatherAsync);
    }
}
