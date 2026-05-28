using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Telemetry;
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
/// Ownership/forwarder/identity are resolved here too so the hybrid sticky-WS +
/// stateless-webhook routing in ADR-0011 only touches the endpoint handlers once.
/// </remarks>
public sealed class CallingServices(
    [FromServices] ICallSessionFactory sessionFactory,
    [FromServices] ICallSessionRegistry sessionRegistry,
    [FromServices] CallAutomationClient callAutomationClient,
    [FromServices] RealtimeIvrWorkflowDefinition workflow,
    [FromServices] IOptions<CommunicationOptions> options,
    [FromServices] ICallOwnershipDirectory ownership,
    [FromServices] IWebhookForwarder webhookForwarder,
    [FromServices] IClusterIdentity clusterIdentity,
    [FromServices] ILoggerFactory loggerFactory,
    [FromServices] ILogger<CallingServices> logger,
    [FromServices] CallingTelemetry telemetry)
{
    public ICallSessionFactory SessionFactory { get; } = sessionFactory;
    public ICallSessionRegistry SessionRegistry { get; } = sessionRegistry;
    public CallAutomationClient CallAutomationClient { get; } = callAutomationClient;
    public RealtimeIvrWorkflowDefinition Workflow { get; } = workflow;
    public IOptions<CommunicationOptions> Options { get; } = options;
    public ICallOwnershipDirectory Ownership { get; } = ownership;
    public IWebhookForwarder WebhookForwarder { get; } = webhookForwarder;
    public IClusterIdentity ClusterIdentity { get; } = clusterIdentity;
    public ILoggerFactory LoggerFactory { get; } = loggerFactory;
    public ILogger<CallingServices> Logger { get; } = logger;
    public CallingTelemetry Telemetry { get; } = telemetry;
}
