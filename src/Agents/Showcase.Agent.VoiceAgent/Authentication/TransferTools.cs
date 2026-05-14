using System.ComponentModel;
using Agents.AI.ContactCenter.IvrWorkflow;
using Microsoft.Extensions.AI;

namespace Showcase.Agent.VoiceAgent.Authentication;

/// <summary>
/// Tools that surface a "transfer to live agent" action from any IVR step. Returning a
/// <see cref="DtmfActionResult.Transfer"/> from the validator/menu tool is interpreted by
/// both DTMF strategies as an <c>OutboundDirective.TransferCall</c>, which the streaming
/// or verb edge dispatches via its <c>ICallControl.TransferAsync</c> surface.
/// </summary>
public static class TransferTools
{
    /// <summary>
    /// Build a tool the menu can bind to (e.g. "press 0 for agent"). Returns a transfer
    /// directive for the configured escalation number with an optional reason.
    /// </summary>
    public static AITool BuildTransferToAgentTool(string escalationNumberE164)
    {
        [Description("Transfer the live call to a human agent at the configured escalation number.")]
        DtmfActionResult TransferToAgent(
            [Description("Brief human-readable reason for the transfer.")]
            string? reason = null)
        {
            return new DtmfActionResult.Transfer(
                TargetIdentifier: escalationNumberE164,
                Kind: TransferKindHint.PhoneNumber,
                Reason: reason ?? "Caller requested an agent");
        }

        return AIFunctionFactory.Create((Delegate)TransferToAgent, name: "transfer_to_agent");
    }
}
