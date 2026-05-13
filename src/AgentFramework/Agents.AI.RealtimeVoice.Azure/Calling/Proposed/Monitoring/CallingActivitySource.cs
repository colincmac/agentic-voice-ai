using System.Diagnostics;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Monitoring;

/// <summary>
/// Shared OpenTelemetry attribute-key constants and activity helpers for the
/// new Calling/Proposed contact-center stack. Intentionally separate from the
/// legacy <c>ConversationSessionActivitySource</c> so the new pipeline can be
/// observed (and alerted on) independently of the old hub.
/// </summary>
internal static class CallingActivitySource
{
    public const string ActivitySourceName = "Agents.AI.ContactCenter.Calling";
    public const string MeterName = "Agents.AI.ContactCenter.Calling";

    // ---- Call-level tags ----------------------------------------------------
    public const string CallIdTag = "call.id";
    public const string CallTierTag = "call.tier";
    public const string CallStrategyKindTag = "call.strategy.kind";
    public const string CallStateTag = "call.state";
    public const string CallStateFromTag = "call.state.from";
    public const string CallStateToTag = "call.state.to";
    public const string CallEndReasonTag = "call.end.reason";

    // ---- Edge tags ----------------------------------------------------------
    public const string EdgeIdTag = "edge.id";
    public const string EdgeKindTag = "edge.kind";
    public const string EdgeCapabilitiesTag = "edge.capabilities";
    public const string EdgeDisconnectReasonTag = "edge.disconnect.reason";

    // ---- Directive / event tags --------------------------------------------
    public const string DirectiveKindTag = "directive.kind";
    public const string StrategyEventKindTag = "strategy.event.kind";

    // ---- Supervisor tags ----------------------------------------------------
    public const string SupervisorIdTag = "supervisor.id";
    public const string SupervisorModeTag = "supervisor.mode";
    public const string SupervisorModeFromTag = "supervisor.mode.from";
    public const string SupervisorModeToTag = "supervisor.mode.to";

    // ---- Transfer / hangup --------------------------------------------------
    public const string TransferKindTag = "transfer.kind";
    public const string TransferTargetTag = "transfer.target";
    public const string HangupForEveryoneTag = "hangup.for_everyone";
    public const string HangupReasonTag = "hangup.reason";

    // ---- Alert tags ---------------------------------------------------------
    public const string AlertIdTag = "alert.id";
    public const string AlertKindTag = "alert.kind";
    public const string AlertSeverityTag = "alert.severity";
    public const string AlertMessageTag = "alert.message";
    public const string AlertResolvedTag = "alert.resolved";

    // ---- Observer tags ------------------------------------------------------
    public const string ObserverIdTag = "observer.id";

    // ---- Activity names -----------------------------------------------------
    public const string CallActivityName = "contact_center.call";
    public const string CreateSessionActivityName = "contact_center.call.create";
    public const string TransitionActivityName = "contact_center.call.transition";
    public const string TransferActivityName = "contact_center.call.transfer";
    public const string HangupActivityName = "contact_center.call.hangup";
    public const string SupervisorAttachActivityName = "contact_center.call.supervisor.attach";
    public const string SupervisorDetachActivityName = "contact_center.call.supervisor.detach";
    public const string SupervisorModeChangeActivityName = "contact_center.call.supervisor.mode_change";
    public const string ReplaceStrategyActivityName = "contact_center.call.replace_strategy";
    public const string DispatchActivityName = "contact_center.edge.dispatch";
    public const string EdgeConnectActivityName = "contact_center.edge.connect";
    public const string EdgeDisconnectActivityName = "contact_center.edge.disconnect";
    public const string StrategyEventActivityName = "contact_center.strategy.event";

    /// <summary>
    /// Marks <paramref name="activity"/> as failed and attaches <c>error.type</c>
    /// / <c>error.message</c> tags so collectors can fire alerts on them.
    /// </summary>
    public static void SetError(Activity? activity, Exception ex)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("error.type", ex.GetType().FullName);
        activity.SetTag("error.message", ex.Message);
        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
    }

    /// <summary>
    /// Adds a structured event to <paramref name="activity"/> describing a
    /// dual-emitted <see cref="QualityAlert"/>. The same event drives Azure
    /// Monitor / Prometheus alerting rules.
    /// </summary>
    public static void RecordAlertEvent(Activity? activity, QualityAlert alert, bool resolved = false)
    {
        if (activity is null)
        {
            return;
        }

        var tags = new ActivityTagsCollection
        {
            { AlertIdTag, alert.AlertId },
            { AlertKindTag, alert.Kind.ToString() },
            { AlertSeverityTag, alert.Severity.ToString() },
            { AlertMessageTag, alert.Message },
            { AlertResolvedTag, resolved },
        };
        activity.AddEvent(new ActivityEvent(
            name: resolved ? "alert.resolved" : "alert.raised",
            timestamp: alert.RaisedAt,
            tags: tags));
    }
}
