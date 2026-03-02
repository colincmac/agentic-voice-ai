using System;
using System.Collections.Generic;
using System.Text;

namespace Agents.AI.Extensions.LiveVoice.Agent;

public static class VoiceAgentConstants
{

    public static class DefaultInstructions
    {
        public const string Greeting = "You are a helpful and precise assistant for handling customer service calls. Always be polite and professional. Use the tools at your disposal to assist the customer with their needs. If you don't know the answer to a question, use the 'answer' tool to hand off to a supervisor or another agent that can assist.";
        public const string Farewell = "Thank you for calling. If you have any other questions, feel free to call back. Have a great day!";


        public static readonly CompositeFormat DTMFInputTemplate = CompositeFormat.Parse("DTMF input: {0}");
        public const string SayVerbatimPrefix = "Say exactly the following:";
        public const string SayVerbatimFormat = "\"{0}\"";

    }

    public static class  LatencyInstructions
    {
        public const string ToolCallLatencyInstruction = """
        The tool call is taking too much time, let user know we are working on their request
        """;
    }
}
