using Agents.AI.Hosting;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using System.ComponentModel;
namespace Showcase.Agent.VoiceAgent.Apis;

public static class TestAgentsApi
{

    [Description("Get the weather for a given location.")]
    static async Task<string> GetWeatherAsync([Description("The location to get the weather for.")] string location)
    {
        await Task.Delay(1000);
        return $"The weather in {location} is cloudy with a high of 15°C.";
    }
    public static WebApplicationBuilder AddTestAgents(this WebApplicationBuilder builder)
    {

        // Configure the chat model and our agent.

        builder.AddAIAgent(
            "pirate",
            (sp, key) =>
            {
                var chatClient = sp.GetRequiredKeyedService<IChatClient>("chat");
                return new ChatClientAgent(
                    chatClient: chatClient,
                    instructions: "You are a pirate. Speak like a pirate",
                    name: "pirate",
                    description: "An agent that speaks like a pirate.",
                    loggerFactory: sp.GetService<ILoggerFactory>(),
                    tools: [AIFunctionFactory.Create(GetWeatherAsync)],
                    services: sp
                    ).AsBuilder()
                    .UseOpenTelemetry("Showcase.VoiceAgent")
                    .Build();
            });

        builder.AddAIAgent("knights-and-knaves", (sp, key) =>
        {
            var chatClient = sp.GetRequiredKeyedService<IChatClient>("chat");

            ChatClientAgent knight = new(
                chatClient,
                """
        You are a knight. This means that you must always tell the truth. Your name is Alice.
        Bob is standing next to you. Bob is a knave, which means he always lies.
        When replying, always start with your name (Alice). Eg, "Alice: I am a knight."
        """, "Alice");

            ChatClientAgent knave = new(
                chatClient,
                """
        You are a knave. This means that you must always lie. Your name is Bob.
        Alice is standing next to you. Alice is a knight, which means she always tells the truth.
        When replying, always include your name (Bob). Eg, "Bob: I am a knight."
        """, "Bob");

            ChatClientAgent narrator = new(
                chatClient,
                """
        You are are the narrator of a puzzle involving knights (who always tell the truth) and knaves (who always lie).
        The user is going to ask questions and guess whether Alice or Bob is the knight or knave.
        Alice is standing to one side of you. Alice is a knight, which means she always tells the truth.
        Bob is standing to the other side of you. Bob is a knave, which means he always lies.
        When replying, always include your name (Narrator).
        Once the user has deduced what type (knight or knave) both Alice and Bob are, tell them whether they are right or wrong.
        If the user asks a general question about their surrounding, make something up which is consistent with the scenario.
        """, "Narrator");

            // TODO: How to avoid sync-over-async here?
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
            return AgentWorkflowBuilder.BuildConcurrent([knight, knave, narrator]).AsAgent(name: key);
#pragma warning restore VSTHRD002
        });
        return builder;
    }

}
