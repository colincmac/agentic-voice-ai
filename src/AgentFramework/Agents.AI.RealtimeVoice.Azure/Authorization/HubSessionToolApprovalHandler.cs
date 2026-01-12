
using Agents.AI.Extensions.ToolApproval;
using Agents.AI.RealtimeVoice.Azure.Calling;

namespace Agents.AI.RealtimeVoice.Azure.Authorization;

public abstract class HubSessionToolApprovalHandler<TRequirement> : ToolApprovalHandler<TRequirement> where TRequirement : IToolApprovalRequirement
{
    protected HubSessionContext HubSessionContext { get; }

    public HubSessionToolApprovalHandler(HubSessionContext hubSessionContext)
    {
        HubSessionContext = hubSessionContext;
    }
}
