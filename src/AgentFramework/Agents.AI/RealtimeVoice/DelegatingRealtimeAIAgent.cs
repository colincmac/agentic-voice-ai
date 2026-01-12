using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Shared.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
namespace Agents.AI.RealtimeVoice;


public class DelegatingRealtimeAIAgent : DelegatingAIAgent, IRealtimeAIAgent
{
    protected DelegatingRealtimeAIAgent(AIAgent innerAgent) : base(innerAgent)
    {
        TypedInnerAgent = InnerAgent.GetService<RealtimeAIAgent>() ?? throw new InvalidOperationException("The inner agent must be derived from a RealtimeAIAgent.");
    }


    protected RealtimeAIAgent TypedInnerAgent;
    /// <inheritdoc />
    public override string Id => InnerAgent.Id;

    /// <inheritdoc />
    public override string? Name => InnerAgent.Name;

    /// <inheritdoc />
    public override string? Description => InnerAgent.Description;

    #region Realtime Methods
    public virtual Task SendAudioToRunAsync(
        DataContent audio,
        AgentThread thread,
        CancellationToken cancellationToken = default)
    {
        return TypedInnerAgent.SendAudioToRunAsync(audio, thread, cancellationToken);
    }

    public virtual Task SendMessagesToRunAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread thread,
        CancellationToken cancellationToken = default)
    {
        return TypedInnerAgent.SendMessagesToRunAsync(messages, thread, cancellationToken);
    }
    public virtual Task<ConversationSessionThread> GetNewThreadAsync(CancellationToken cancellationToken = default) => TypedInnerAgent.GetNewThreadAsync(cancellationToken);

    #endregion

    #region AIAgent Methods
    /// <inheritdoc />
    public override AgentThread GetNewThread() => InnerAgent.GetNewThread();

    /// <inheritdoc />
    public override AgentThread DeserializeThread(JsonElement serializedThread, JsonSerializerOptions? jsonSerializerOptions = null)
        => InnerAgent.DeserializeThread(serializedThread, jsonSerializerOptions);

    /// <inheritdoc />
    public override Task<AgentRunResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => InnerAgent.RunAsync(messages, thread, options, cancellationToken);
    
        

    /// <inheritdoc />
    public override IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => InnerAgent.RunStreamingAsync(messages, thread, options, cancellationToken);

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        _ = Throw.IfNull(serviceType);

        // If the key is non-null, we don't know what it means so pass through to the inner service.
        return
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this :
            InnerAgent.GetService(serviceType, serviceKey);
    }

    public Task<AgentRunResponse?> CancelRunAsync(string id, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        return TypedInnerAgent.CancelRunAsync(id, options, cancellationToken);
    }

    public Task<AgentRunResponse?> DeleteRunAsync(string id, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        return TypedInnerAgent.DeleteRunAsync(id, options, cancellationToken);
    }

    #endregion
}
