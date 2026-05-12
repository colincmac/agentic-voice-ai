using Agents.AI.Extensions.LiveVoice.IvrWorkflow;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Implementation;

internal static class WorkflowStateRestore
{
    public static void CopyInto(IvrWorkflowState source, IvrWorkflowState target)
    {
        target.CurrentStepName = source.CurrentStepName;
        target.Status = source.Status;
        target.TotalTurns = source.TotalTurns;

        foreach (var stepId in source.CompletedSteps)
        {
            target.MarkStepCompleted(stepId);
        }

        foreach (var key in source.Keys)
        {
            target.Set(key, source.Get<object>(key));
        }
    }
}
