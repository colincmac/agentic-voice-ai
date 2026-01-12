using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Extensions.AI.OpenTelemetry.SemanticConventions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.RealtimeVoice;

/// Both the  <see cref="OpenTelemetryAgent"/> and  <see cref="FunctionInvocationDelegatingAgent"/> require the use of chat clients like the <see cref="OpenTelemetryChatClient"/> and the <see cref="FunctionInvokingChatClient"/>
/// So we need to write our own wrappers
public class OpenTelemetryRealtimeAgent : DelegatingRealtimeAIAgent
{

    private readonly string? _providerName;
    private readonly string? _sourceName;

    public OpenTelemetryRealtimeAgent(RealtimeAIAgent innerAgent, string? sourceName = null) : base(innerAgent)
    {
        this._providerName = innerAgent.GetService<AIAgentMetadata>()?.ProviderName;
        _sourceName = sourceName;
    }

    /// <inheritdoc />
    public override async Task<AgentRunResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await this.TypedInnerAgent.RunAsync(messages, thread, options, cancellationToken);
        if(Activity.Current is { } activity)
        {
            UpdateCurrentActivity(activity);
        }
        return result;
    }
        

    /// <inheritdoc />
    public override IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var results = this.TypedInnerAgent.RunStreamingAsync(messages, thread, options, cancellationToken);
        if (Activity.Current is { } activity)
        {
            UpdateCurrentActivity(activity);
        }
        return results;
    }
        

    /// <summary>Augments the current activity created by the <see cref="OpenTelemetryChatClient"/> with agent-specific information.</summary>
    /// <param name="previousActivity">The <see cref="Activity"/> that was current prior to the <see cref="OpenTelemetryChatClient"/>'s invocation.</param>
    private void UpdateCurrentActivity(Activity? previousActivity)
    {
        // If there isn't a current activity to augment, or it's the same one that was current when the agent was invoked (meaning
        // the OpenTelemetryChatClient didn't create one), then there's nothing to do.
        if (Activity.Current is not { } activity ||
            ReferenceEquals(activity, previousActivity))
        {
            return;
        }

        // Override information set by OpenTelemetryChatClient to make it specific to invoke_agent.

        activity.DisplayName = $"{GenAI.OperationNameValues.InvokeAgent} {this.DisplayName}";

        if (!string.IsNullOrWhiteSpace(this._providerName))
        {
            _ = activity.SetTag(GenAI.AttributeGenAiProviderName, this._providerName);
        }

        // Further augment the activity with agent-specific tags.

        _ = activity.SetTag(GenAI.AttributeGenAiAgentId, this.Id);

        if (this.Name is { } name && !string.IsNullOrWhiteSpace(name))
        {
            _ = activity.SetTag(GenAI.AttributeGenAiAgentName, this.Name);
        }

        if (this.Description is { } description && !string.IsNullOrWhiteSpace(description))
        {
            _ = activity.SetTag(GenAI.AttributeGenAiAgentDescription, description);
        }
    }
}
