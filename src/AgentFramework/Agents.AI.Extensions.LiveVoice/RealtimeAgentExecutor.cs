using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.RealtimeVoice;
using Extensions.AI.Contents;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.LiveVoice;

// Supporting Dynamic Conversation Flows
public abstract class IvrStepExecutor : StatefulExecutor<List<ChatMessage>>
{
    private readonly AuthorizingRealtimeAIAgent _agent;
    private LiveConversationAgentSession? _thread;
    private static readonly Func<List<ChatMessage>> initFunction = () => [];
    private const string ThreadStateKey = nameof(_thread);

    public IvrStepExecutor(AuthorizingRealtimeAIAgent agent, bool declareCrossRunShareable = false) : base(agent.Id, () => [], declareCrossRunShareable: declareCrossRunShareable)
    {
        _agent = agent;
    }

    /// <inheritdoc/>
    protected override RouteBuilder ConfigureRoutes(RouteBuilder routeBuilder)
    {

        return routeBuilder.AddHandler<ChatMessage>(this.AddMessageAsync)
                           .AddHandler<IEnumerable<ChatMessage>>(this.AddMessagesAsync)
                           .AddHandler<ChatMessage[]>(this.AddMessagesAsync)
                           .AddHandler<List<ChatMessage>>(this.AddMessagesAsync)
                           .AddHandler<ReadOnlyMemory<byte>>(this.SendAudioAsync)
                           .AddHandler<DataContent>(this.SendDataContentAsync)
                           .AddHandler<TurnToken>(this.TakeTurnAsync);
    }

    private async Task<LiveConversationAgentSession> EnsureThreadAsync(CancellationToken cancellationToken = default) {

        return _thread ??= await _agent.GetNewSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    protected async ValueTask SendAudioAsync(ReadOnlyMemory<byte> frame, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var thread = await EnsureThreadAsync(cancellationToken);

        await _agent.SendAudioToRunAsync(new DataContent(frame, "audio/pcm"), thread, cancellationToken);
    }

    protected override async ValueTask OnCheckpointRestoredAsync(IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        JsonElement? threadValue = await context.ReadStateAsync<JsonElement?>(ThreadStateKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (threadValue.HasValue)
        {
            var thread = this._agent.DeserializeThread(threadValue.Value);
            if(thread is not LiveConversationAgentSession threadSession)
            {
               throw new InvalidOperationException($"Deserialized thread is not of expected type {nameof(LiveConversationAgentSession)}");
            }
            this._thread = threadSession;
        }

        await base.OnCheckpointRestoredAsync(context, cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask OnCheckpointingAsync(IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        Task threadTask = Task.CompletedTask;
        if (this._thread is not null)
        {
            JsonElement threadValue = this._thread.Serialize();
            threadTask = context.QueueStateUpdateAsync(ThreadStateKey, threadValue, cancellationToken: cancellationToken).AsTask();
        }

        Task baseTask = base.OnCheckpointingAsync(context, cancellationToken).AsTask();

        await Task.WhenAll(threadTask, baseTask).ConfigureAwait(false);
    }

    public async ValueTask SendDataContentAsync(DataContent dataContent, IWorkflowContext context, CancellationToken cancellationToken)
    {
        var thread = await EnsureThreadAsync(cancellationToken);
        await _agent.SendAudioToRunAsync(dataContent, thread, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a single chat message to the accumulated messages for the current turn.
    /// </summary>
    /// <param name="message">The chat message to add.</param>
    /// <param name="context">The workflow context in which the executor executes.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
    protected async ValueTask AddMessageAsync(ChatMessage message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var thread = await EnsureThreadAsync(cancellationToken);
        await _agent.SendMessagesToRunAsync([message], thread, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds multiple chat messages to the accumulated messages for the current turn.
    /// </summary>
    /// <param name="messages">The collection of chat messages to add.</param>
    /// <param name="context">The workflow context in which the executor executes.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
    protected async ValueTask AddMessagesAsync(IEnumerable<ChatMessage> messages, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var thread = await EnsureThreadAsync(cancellationToken);

        await _agent.SendMessagesToRunAsync(messages, thread, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a turn token by processing all accumulated chat messages and then resetting the message state.
    /// </summary>
    /// <param name="token">The turn token that triggers message processing.</param>
    /// <param name="context">The workflow context in which the executor executes.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
    public ValueTask TakeTurnAsync(TurnToken token, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        return this.InvokeWithStateAsync(InvokeTakeTurnAsync, context, cancellationToken: cancellationToken);

        async ValueTask<List<ChatMessage>?> InvokeTakeTurnAsync(List<ChatMessage>? maybePendingMessages, IWorkflowContext context, CancellationToken cancellationToken)
        {
            await this.TakeTurnAsync(maybePendingMessages ?? initFunction(), context, token.EmitEvents, cancellationToken)
                      .ConfigureAwait(false);

            await context.SendMessageAsync(token, cancellationToken: cancellationToken).ConfigureAwait(false);

            // Rerun the initialStateFactory to reset the state to empty list. (We could return the empty list directly,
            // but this is more consistent if the initial state factory becomes more complex.)
            return initFunction();
        }
    }

    private async ValueTask TakeTurnAsync(List<ChatMessage> messages, IWorkflowContext context, bool? emitEvents, CancellationToken cancellationToken = default)
    {
        List<AgentRunResponseUpdate> updates = [];
        await foreach (var update in _agent.RunStreamingAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
            foreach(var content in update.Contents)
            {
                if (content is RealtimeResponseFinishedContent finishedContent)
                {
                    var agentResponse = updates.ToAgentRunResponse();
                    updates.Clear();
                    var turn = new RealtimeConversationTurn
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        AgentRun = agentResponse,
                        Metadata = new Dictionary<string, object>
                        {
                            { "ReferenceItemId", finishedContent.ReferenceItemId ?? string.Empty }
                        }
                    };
                    await context.SendMessageAsync(turn, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
            if (emitEvents is true)
            {
                await context.AddEventAsync(new AgentRunUpdateEvent(this.Id, update), cancellationToken).ConfigureAwait(false);
            }
        }
    }

}
