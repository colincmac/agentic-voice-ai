//using Agents.AI.Extensions.Helpers.Streaming;
//using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
//using Extensions.AI.Contents;
//using Microsoft.Agents.AI;
//using Microsoft.Agents.AI.Workflows;
//using Microsoft.Extensions.AI;
//using Microsoft.Shared.Diagnostics;

//namespace Agents.AI.Extensions.LiveVoice.Workflows;

///// <summary>
///// Batches AgentResponseUpdate messages and forwards them as a <see cref="RealtimeVoiceAgentTurn"/> every time it encounters <see cref="RealtimeResponseFinishedContent"/>
///// </summary>
//public sealed class AgentUpdateTrackingExecutor : StatefulExecutor<List<AgentResponseUpdate>>, IResettableExecutor
//{
//    private static readonly Func<List<AgentResponseUpdate>> initFunction = () => [];

//    public AgentUpdateTrackingExecutor(string id) : base(id, initFunction, declareCrossRunShareable: true)
//    {

//    }

//    protected override RouteBuilder ConfigureRoutes(RouteBuilder routeBuilder)
//    {
//        return routeBuilder.AddHandler<MessageUpdate>(this.HandleMessageUpdateAsync);
//    }

//    public ValueTask HandleMessageUpdateAsync(MessageUpdate message, IWorkflowContext context, CancellationToken cancellationToken = default)
//    {
//        return this.InvokeWithStateAsync(ForwardMessageAsync, context, cancellationToken: cancellationToken);

//        async ValueTask<List<AgentResponseUpdate>?> ForwardMessageAsync(List<AgentResponseUpdate>? maybePendingMessages, IWorkflowContext context, CancellationToken cancelationToken)
//        {
//            maybePendingMessages ??= initFunction();
//            if(message.RawRepresentation is AgentResponseUpdate agentMessage)
//            {

//                maybePendingMessages.Add(agentMessage);
//                foreach (var content in agentMessage.Contents)
//                {
//                    if (content is RealtimeResponseFinishedContent aiResponseFinished)
//                    {
//                        var chatResponse = maybePendingMessages.Select(u => AsChatResponseUpdate(u)).ToChatResponse();
//                        await context.SendMessageAsync(new RealtimeVoiceAgentTurn([.. chatResponse.Messages]), cancelationToken);
//                        maybePendingMessages = initFunction();
//                    }
//                }
//            }

//            return [.. maybePendingMessages];
//        }
//    }

//    ValueTask IResettableExecutor.ResetAsync() => this.ResetAsync();

//    private static ChatResponseUpdate AsChatResponseUpdate(AgentResponseUpdate responseUpdate)
//    {
//        Throw.IfNull(responseUpdate);
//        return
//            responseUpdate.RawRepresentation as ChatResponseUpdate ??
//            new()
//            {
//                AdditionalProperties = responseUpdate.AdditionalProperties,
//                AuthorName = responseUpdate.AuthorName,
//                Contents = responseUpdate.Contents,
//                CreatedAt = responseUpdate.CreatedAt,
//                MessageId = responseUpdate.MessageId,
//                RawRepresentation = responseUpdate,
//                ResponseId = responseUpdate.ResponseId,
//                Role = responseUpdate.Role,
//            };
//    }
//}
