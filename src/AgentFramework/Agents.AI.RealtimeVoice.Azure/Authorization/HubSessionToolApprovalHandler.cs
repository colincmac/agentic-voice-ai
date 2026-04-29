
using Agents.AI.Extensions.ToolApproval;
using Agents.AI.RealtimeVoice.Azure.Calling;

namespace Agents.AI.RealtimeVoice.Azure.Authorization;

public abstract class HubSessionToolApprovalHandler<TRequirement> : ToolApprovalHandler<TRequirement> where TRequirement : IToolApprovalRequirement
{
    protected HubSessionContext HubSessionContext { get; }

    /// <summary>
    /// Convenience accessor for resolving services from the session scope.
    /// </summary>
    protected IServiceProvider SessionServices => HubSessionContext.SessionServices;

    public HubSessionToolApprovalHandler(HubSessionContext hubSessionContext)
    {
        HubSessionContext = hubSessionContext;
    }
}
