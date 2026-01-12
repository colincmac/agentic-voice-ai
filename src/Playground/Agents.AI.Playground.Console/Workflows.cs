using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Agents.AI.Playground.ConsoleApp;

public static class Workflows
{
    public async static Task Run(AIAgent realtime, AIAgent chat, CancellationToken ct)
    {
        ChatForwardingExecutor start = new("Start");
        WorkflowBuilder builder = new(start);

        var workflow = AgentWorkflowBuilder.BuildConcurrent([realtime, chat]);
        List<ChatMessage> messages = [new(ChatRole.User, "Hello, world!")];
        await using var run = await InProcessExecution.StreamAsync(workflow, messages, cancellationToken: ct);
        List<ChatMessage> result = new();
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(ct).ConfigureAwait(false))
        {
            if (evt is AgentRunUpdateEvent e)
            {
                Console.WriteLine($"{e.ExecutorId}: {e.Data}");
            }
            else if (evt is WorkflowOutputEvent outputEvt)
            {
                result = (List<ChatMessage>)outputEvt.Data!;
                break;
            }
        }
    }
}
