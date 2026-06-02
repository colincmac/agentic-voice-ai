using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Telemetry;
using Azure.Communication.CallAutomation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Showcase.Agent.VoiceAgent.Workflow;

namespace Showcase.Agent.VoiceAgent.Apis;

/// <summary>
/// Services injected into <see cref="CallingApi"/> endpoint handlers via [AsParameters].
/// </summary>
/// <remarks>
/// Pulls from <see cref="ICallSessionFactory"/> + <see cref="ICallSessionRegistry"/> for
/// per-call lifecycle, and from <see cref="CallEntryConfig"/> for the workflow id +
/// initial tier the showcase routes new calls to.
/// </remarks>
public sealed class CallingServices(
    [FromServices] ICallSessionFactory sessionFactory,
    [FromServices] ICallSessionRegistry sessionRegistry,
    [FromServices] CallAutomationClient callAutomationClient,
    [FromServices] CallEntryConfig entryConfig,
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
    public CallEntryConfig EntryConfig { get; } = entryConfig;
    public IOptions<CommunicationOptions> Options { get; } = options;
    public ICallOwnershipDirectory Ownership { get; } = ownership;
    public IWebhookForwarder WebhookForwarder { get; } = webhookForwarder;
    public IClusterIdentity ClusterIdentity { get; } = clusterIdentity;
    public ILoggerFactory LoggerFactory { get; } = loggerFactory;
    public ILogger<CallingServices> Logger { get; } = logger;
    public CallingTelemetry Telemetry { get; } = telemetry;
}
