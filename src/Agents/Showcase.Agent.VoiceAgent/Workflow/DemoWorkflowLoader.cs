using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;

namespace Showcase.Agent.VoiceAgent.Workflow;

/// <summary>
/// Helper for resolving showcase IVR workflows from the DI-registered
/// <see cref="IIvrWorkflowLoader"/> by id. The loader compiles the YAML files
/// under <c>Workflow/Samples/</c> into <see cref="RealtimeIvrWorkflowDefinition"/>
/// instances that the existing strategy stack consumes directly.
/// </summary>
/// <remarks>
/// Workflow ids match the <c>name:</c> field at the root of each YAML file:
/// <list type="bullet">
///   <item><c>dtmf-direct-express</c> — pure DTMF Direct Express style menu (port of <c>IvrSampleWorkflow.DtmfOnly</c>).</item>
///   <item><c>caller-intent-biometric</c> — realtime ACME Financial caller-intent + biometric flow (port of <c>ConversationWorkflowFactory.CreateCallerIntentWorkflow</c>).</item>
///   <item><c>authenticated-dtmf</c> — PIN-gated DTMF banking flow (port of <c>AuthenticatedSampleWorkflows.BuildAuthenticatedDtmfWorkflow</c>).</item>
///   <item><c>authenticated-realtime</c> — voice-first ACME Bank concierge with PIN (port of <c>AuthenticatedSampleWorkflows.BuildAuthenticatedRealtimeWorkflow</c>).</item>
///   <item><c>nlu-with-dtmf-fallback</c> — ACME Bank concierge that classifies intents via intent-NLU and degrades to pure DTMF for PIN entry and menu routing.</item>
/// </list>
/// </remarks>
public static class DemoWorkflowIds
{
    public const string DefaultEscalationNumber = "+15555550199";

    /// <summary>Directory (relative to <see cref="AppContext.BaseDirectory"/>) where the showcase YAML samples are copied at build time.</summary>
    public const string SamplesDirectory = "Workflow/Samples";

    public const string DtmfDirectExpress = "dtmf-direct-express";
    public const string CallerIntentBiometric = "caller-intent-biometric";
    public const string AuthenticatedDtmf = "authenticated-dtmf";
    public const string AuthenticatedRealtime = "authenticated-realtime";
    public const string NluWithDtmfFallback = "nlu-with-dtmf-fallback";
}

/// <summary>
/// DI-friendly helper that loads a <see cref="RealtimeIvrWorkflowDefinition"/> from the
/// declarative <see cref="IIvrWorkflowLoader"/>. Blocks on the underlying
/// <see cref="ValueTask"/> because <see cref="RealtimeIvrWorkflowDefinition"/> is registered
/// as a singleton during host startup, where blocking on a synchronous file-system source
/// is safe.
/// </summary>
public static class DemoWorkflowLoader
{
    public static RealtimeIvrWorkflowDefinition Load(IServiceProvider services, string workflowId)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(workflowId);

        var loader = services.GetService(typeof(IIvrWorkflowLoader)) as IIvrWorkflowLoader
            ?? throw new InvalidOperationException(
                $"No IIvrWorkflowLoader is registered. Call services.AddIvrWorkflowFramework(builder => builder.AddFileSystemSource(\"{DemoWorkflowIds.SamplesDirectory}\")) in Program.cs.");

        var compiled = loader.LoadAsync(workflowId).AsTask().GetAwaiter().GetResult();
        return compiled.Runtime;
    }
}
