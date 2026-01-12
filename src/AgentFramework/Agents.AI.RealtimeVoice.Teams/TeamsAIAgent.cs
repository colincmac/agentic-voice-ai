using Microsoft.Agents.AI;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.AI;

namespace Agents.AI.RealtimeVoice.Teams;

public class TeamsAIAgent : AgentApplication
{
    private readonly AIAgent _innerAgent;

    /// <summary>
    /// Example of a streaming response agent using the Azure OpenAI ChatClient.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="chatClient"></param>
    public TeamsAIAgent(AgentApplicationOptions options, AIAgent innerAgent) : base(options)
    {
        _innerAgent = innerAgent;

        // Register an event to welcome new channel members.

        // Register an event to handle messages from the client.
        OnActivity(ActivityTypes.Message, OnMessageAsync, rank: RouteRank.Last);
    }

    /// <returns></returns>
    public virtual async Task OnMessageAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        try
        {
            // Raise an informative update to the calling client,  if the client support StreamingResponses this will appear as a contextual notification. 
            await turnContext.StreamingResponse.QueueInformativeUpdateAsync("Thinking...", cancellationToken);
            List<ChatMessage> messages =
            [
                new ChatMessage(ChatRole.User, turnContext.Activity.Text),
            ];

            // Requesting the connected LLM Model to do work :) 
            await foreach (var update in _innerAgent.RunStreamingAsync(messages: messages, cancellationToken: cancellationToken))
            {
                if (update.Contents.Count > 0)
                {
                    if (!string.IsNullOrEmpty(update.Text))
                        turnContext.StreamingResponse.QueueTextChunk(update.Text);
                }
            }
        }
        finally
        {
            // Signal that your done with this stream. 
            await turnContext.StreamingResponse.EndStreamAsync(cancellationToken);
        }
    }
}
