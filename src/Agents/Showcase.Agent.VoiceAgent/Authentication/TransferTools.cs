using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Showcase.Agent.VoiceAgent.Authentication;

/// <summary>
/// Tools that surface a "transfer to live agent" action from any IVR step. The new
/// realtime strategy logs the request and surfaces it back to the model as a
/// confirmation string; wiring through to <c>ICallControl.TransferAsync</c> is the
/// next integration step.
/// </summary>
public static class TransferTools
{
    /// <summary>
    /// Build a tool the menu can bind to (e.g. "press 0 for agent"). Returns a short
    /// acknowledgement string that the model can verbalize to the caller before the
    /// strategy completes the transfer.
    /// </summary>
    public static AITool BuildTransferToAgentTool(string escalationNumberE164)
    {
        [Description("Transfer the live call to a human agent at the configured escalation number.")]
        string TransferToAgent(
            [Description("Brief human-readable reason for the transfer.")]
            string? reason = null)
        {
            var why = reason ?? "Caller requested an agent";
            return $"Transferring to {escalationNumberE164}: {why}";
        }

        return AIFunctionFactory.Create((Delegate)TransferToAgent, name: "transfer_to_agent");
    }
}
