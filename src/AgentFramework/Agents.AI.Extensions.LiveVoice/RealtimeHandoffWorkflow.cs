using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Agents.AI.RealtimeVoice;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.LiveVoice;

/// <summary>
/// A specialized workflow manager for Realtime Agents that run indefinitely.
/// It orchestrates switching between different Realtime Agents based on handoff tool calls.
/// </summary>
public class RealtimeHandoffWorkflow
{
    private readonly Dictionary<string, RealtimeAIAgent> _agents = new();
    private readonly Dictionary<string, List<HandoffTarget>> _handoffMap = new();
    private readonly string _defaultAgentId;

    // Schema for the handoff tool
    private static readonly JsonElement handoffSchema = AIFunctionFactory.Create(
        ([Description("The reason for the handoff")] string? reasonForHandoff) => { }).JsonSchema;

    public RealtimeHandoffWorkflow(RealtimeAIAgent initialAgent)
    {
        _defaultAgentId = initialAgent.Id;
        RegisterAgent(initialAgent);
    }

    /// <summary>
    /// Registers a handoff path from one agent to another.
    /// </summary>
    public void AddHandoff(RealtimeAIAgent from, RealtimeAIAgent to, string? description = null)
    {
        RegisterAgent(from);
        RegisterAgent(to);

        if (!_handoffMap.ContainsKey(from.Id))
        {
            _handoffMap[from.Id] = new List<HandoffTarget>();
        }

        string reason = description ?? to.Description ?? to.Name;
        _handoffMap[from.Id].Add(new HandoffTarget(to, reason));
    }

    private void RegisterAgent(RealtimeAIAgent agent)
    {
        if (!_agents.ContainsKey(agent.Id))
        {
            _agents[agent.Id] = agent;
        }
    }

    /// <summary>
    /// Runs the workflow. This method will run indefinitely until cancellation.
    /// It manages the lifecycle of the active agent and handles transitions.
    /// </summary>
    public async IAsyncEnumerable<AgentRunResponseUpdate> RunAsync(
        IEnumerable<ChatMessage> initialMessages,
        TranscriptTrackingAgentThread thread,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string currentAgentId = _defaultAgentId;

        // We loop strictly for Handoffs. The inner loop is the infinite stream of the current agent.
        while (!cancellationToken.IsCancellationRequested)
        {
            var currentAgent = _agents[currentAgentId];
            var handoffs = _handoffMap.TryGetValue(currentAgentId, out var targets) ? targets : new List<HandoffTarget>();

            // Create options with Handoff Tools injected
            var options = CreateOptionsWithHandoffs(handoffs);

            Console.WriteLine($"[System] Starting Agent: {currentAgent.Name}");

            string? nextAgentId = null;

            // Run the current agent's infinite stream
            // We await this loop; it only breaks if the connection dies or a handoff is triggered
            await foreach (var update in currentAgent.RunStreamingAsync(initialMessages, thread, options, cancellationToken))
            {

                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent fcc)
                    {
                        // Check if this function call matches any of our handoff tools
                        foreach (var t in handoffs)
                        {
                            var funcName = $"handoff_to_{Sanitize(t.Target.Name)}";
                            if (fcc.Name.Equals(funcName, StringComparison.OrdinalIgnoreCase))
                            {
                                nextAgentId = t.Target.Id;
                            }
                        }
                    }
                }
                // Check if a handoff tool was called
                if (TryDetectHandoff(update, handoffs, out var targetId))
                {
                    nextAgentId = targetId;

                    // Logic to gracefully stop the current agent's session is implicitly handled 
                    // because we will break the foreach loop here.
                    // Ideally, RealtimeAIAgent listens to a cancellation token we control, 
                    // or we break here and the agent cleans up.
                    break;
                }
                yield return update;
            }

            // Transition logic
            if (nextAgentId != null)
            {
                currentAgentId = nextAgentId;

                // Optional: Inject a "context switch" system message into the thread for the next agent
                // initialMessages = [ new ChatMessage(ChatRole.System, $"Transferred to {currentAgentId}") ];

                // IMPORTANT: Ensure the previous session is closed/disposed so the audio stream releases
                // This depends on how RealtimeAIAgent handles the break of iteration.
            }
            else
            {
                // If we exit the loop without a handoff, the session ended naturally or errored.
                // We typically stop the whole workflow.
                break;
            }
        }
    }

    private RealtimeAgentRunOptions CreateOptionsWithHandoffs(List<HandoffTarget> targets)
    {
        var options = new RealtimeAgentRunOptions
        {
            ResponseOptions = new LiveConversationResponseOptions
            {
                Tools = new List<AITool>()
            }
        };

        // Inject tools dynamically
        foreach (var t in targets)
        {
            var funcName = $"handoff_to_{Sanitize(t.Target.Name)}";
            var tool = AIFunctionFactory.CreateDeclaration(
                funcName,
                $"Transfer conversation to {t.Target.Name}. {t.Reason}",
                handoffSchema
            );
            options.ResponseOptions.Tools.Add(tool);
        }

        return options;
    }

    private bool TryDetectHandoff(AgentRunResponseUpdate update, List<HandoffTarget> targets, out string? targetAgentId)
    {
        targetAgentId = null;
        if (update.Contents == null) return false;

        foreach (var content in update.Contents)
        {
            if (content is FunctionCallContent fcc)
            {
                // Check if this function call matches any of our handoff tools
                foreach (var t in targets)
                {
                    var funcName = $"handoff_to_{Sanitize(t.Target.Name)}";
                    if (fcc.Name.Equals(funcName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetAgentId = t.Target.Id;
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static string Sanitize(string name) => name.Replace(" ", "_").ToLowerInvariant();

    public record HandoffTarget(RealtimeAIAgent Target, string Reason);
}
