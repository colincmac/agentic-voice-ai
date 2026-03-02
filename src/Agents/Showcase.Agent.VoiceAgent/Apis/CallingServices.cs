using Agents.AI.RealtimeVoice.Azure.Calling;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Azure.Communication.CallAutomation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
namespace Showcase.Agent.VoiceAgent.Apis;

//public class CallingServices([FromServices] CallAutomationClient callAutomationClient, [FromKeyedServices(AgentConfig.TriageAgent)] RealtimeAIAgent Agent, IOptions <CommunicationOptions> options, ILogger<CallingServices> logger)
//{
//    public CallAutomationClient CallAutomationClient { get; } = callAutomationClient;
//    public IOptions<CommunicationOptions> Options { get; } = options;
//    public ILogger<CallingServices> Logger { get; } = logger;
//    public RealtimeAIAgent Agent { get; } = Agent;
//}
public class CallingServices([FromServices] ContactCenterConversationHub conversationHub, [FromServices] CallAutomationClient callAutomationClient, IOptions<CommunicationOptions> options, ILogger<CallingServices> logger)
{
    public CallAutomationClient CallAutomationClient { get; } = callAutomationClient;
    public IOptions<CommunicationOptions> Options { get; } = options;
    public ILogger<CallingServices> Logger { get; } = logger;
    public ContactCenterConversationHub ConversationHub { get; } = conversationHub;
}
