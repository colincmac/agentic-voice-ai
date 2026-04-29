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

    [Description("Activate user credit card information by user pin. Retry ONCE if the user provides incorrect information. Returns true if the user exists.")]
    public Task<bool> ActivateCreditCardAsync([Description("The account ID of the user to look up.")] string accountId, [Description("The users pin provided to them in the letter they recieved with the card")] string userPin, CancellationToken token = default)
    {
        // Implementation for looking up user information
        return Task.FromResult(accountId == "8888" && userPin == "1234");
    }

    [Description("Transfer the user to a human agent.")]
    public Task<string> TransferToHumanAsync(CancellationToken token = default)
    {
        // Implementation for looking up user information
        return Task.FromResult("Escalating to a support agent.");
    }

    [Description("Send a pin to the user's account phone number.")]
    public Task<string> SendSmsPinAsync(CancellationToken token = default)
    {
        // Implementation for looking up user information
        return Task.FromResult("Pin sent");
    }

    public IEnumerable<AITool> AsAITools()
    {
        yield return AIFunctionFactory.Create(GetWeatherAsync);
    }
}
