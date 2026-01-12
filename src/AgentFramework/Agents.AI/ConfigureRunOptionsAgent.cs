using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Agents.AI;
public class ConfigureRunOptionsAgent : DelegatingAIAgent
{
    private readonly Action<AgentRunOptions?> _configureRunOptions;

    public ConfigureRunOptionsAgent(AIAgent innerAgent, Action<AgentRunOptions?> configureRunOptions) : base(innerAgent)
    {
        _configureRunOptions = configureRunOptions;
    }

    public override Task<AgentRunResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.InnerAgent.RunAsync(messages, thread, ConfigureRunOptions(options), cancellationToken);

    public override IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
            => this.InnerAgent.RunStreamingAsync(messages, thread, ConfigureRunOptions(options), cancellationToken);

    /// <summary>Creates and configures the <see cref="ChatOptions"/> to pass along to the inner client.</summary>
    private AgentRunOptions ConfigureRunOptions(AgentRunOptions? options)
    {
        options = options ?? new();
  
        _configureRunOptions(options);

        return options;
    }
}
