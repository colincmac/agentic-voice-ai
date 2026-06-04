using Agents.AI.ContactCenter.Configuration;

namespace Showcase.Agent.VoiceAgent.Workflow;

/// <summary>
/// Showcase-wide constants. Replaces <c>DemoWorkflowIds</c> from the legacy IVR framework.
/// </summary>
public static class ShowcaseWorkflowIds
{
    /// <summary>Number the showcase escalates to when a caller asks for a live agent.</summary>
    public const string DefaultEscalationNumber = "+15555550199";

    /// <summary>Directory (relative to the app's output) that holds new-model YAML blueprints.</summary>
    public const string SamplesDirectory = "Workflow/Samples";

    /// <summary>Id of the canonical end-to-end workflow shipped with the showcase.</summary>
    public const string AuthenticatedRealtimeBank = "authenticated-realtime-bank";
}
