using System;
using System.Collections.Generic;
using System.Text;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.Realtime;

public class RealtimeAgentRunOptions : AgentRunOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RealtimeAgentRunOptions"/> class.
    /// </summary>
    /// <param name="chatOptions">Optional chat options to pass to the agent's invocation.</param>
    public RealtimeAgentRunOptions(RealtimeSessionOptions? sessionOptions = null)
    {
        this.SessionOptions = sessionOptions;
    }

    public bool InitiateConversation { get; set; } = true;

    /// <summary>Gets or sets optional response options to pass to the agent's invocation.</summary>
    public RealtimeSessionOptions? SessionOptions { get; set; }

    /// <summary>
    /// Optional predicate evaluated for every <see cref="AgentRunResponseUpdate"/>.
    /// When it returns true:
    /// 1. The underlying realtime session receive loop is cancelled.
    /// 2. Streaming stops (the async enumerable completes).
    /// Use this to stop after a condition (e.g. first assistant message, certain tool call, keyword match, etc.).
    /// ex: `update => update.Contents.Any(c => c is RealtimeResponseFinishedContent)`
    /// </summary>
    public Func<AgentResponseUpdate, bool> TerminationPredicate { get; set; } = _ => false;


    /// <summary>
    /// Gets or sets a factory function that can replace (typically via decorators) the chat client on a per-request basis.
    /// </summary>
    /// <value>
    /// A function that receives the agent's configured chat client and returns a potentially modified or entirely
    /// different chat client to use for this specific invocation. If <see langword="null"/>, the agent's default
    /// chat client will be used without modification.
    /// </value>
    public Func<IRealtimeClient, IRealtimeClient>? ConversationClientFactory { get; set; }
}
