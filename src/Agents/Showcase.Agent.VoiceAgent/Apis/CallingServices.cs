using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.RealtimeVoice.Azure.Calling.Proposed;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Azure.Communication.CallAutomation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Showcase.Agent.VoiceAgent.Apis;

/// <summary>
/// Services injected into <see cref="CallingApi"/> endpoint handlers via [AsParameters].
/// Pulls from the new <see cref="ICallSessionFactory"/> + <see cref="ICallSessionRegistry"/>
/// shape rather than the legacy <c>ContactCenterConversationHub</c>.
/// </summary>
/// <remarks>
/// Workflows are resolved per-tier so the showcase can demo both DTMF and Realtime flows
/// from the same incoming-call endpoint by selecting a tier at request time.
/// </remarks>
public sealed class CallingServices(
    [FromServices] ICallSessionFactory sessionFactory,
    [FromServices] ICallSessionRegistry sessionRegistry,
    [FromServices] CallAutomationClient callAutomationClient,
    [FromServices] RealtimeIvrWorkflowDefinition workflow,
    [FromServices] IServiceProvider services,
    [FromServices] IOptions<CommunicationOptions> options,
    [FromServices] ILoggerFactory loggerFactory,
    [FromServices] ILogger<CallingServices> logger)
{
    public ICallSessionFactory SessionFactory { get; } = sessionFactory;
    public ICallSessionRegistry SessionRegistry { get; } = sessionRegistry;
    public CallAutomationClient CallAutomationClient { get; } = callAutomationClient;
    public RealtimeIvrWorkflowDefinition Workflow { get; } = workflow;
    public IOptions<CommunicationOptions> Options { get; } = options;
    public ILoggerFactory LoggerFactory { get; } = loggerFactory;
    public ILogger<CallingServices> Logger { get; } = logger;

    /// <summary>
    /// Resolve the workflow registered for <paramref name="tier"/> if one was registered as a
    /// keyed service (key = tier name). Falls back to the unkeyed <see cref="Workflow"/>.
    /// </summary>
    public RealtimeIvrWorkflowDefinition WorkflowFor(AgentTier tier)
        => services.GetKeyedService<RealtimeIvrWorkflowDefinition>(tier.ToString()) ?? Workflow;
}
