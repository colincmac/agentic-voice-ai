using System.ComponentModel;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.IvrWorkflow;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Showcase.Agent.VoiceAgent.Tools;

/// <summary>
/// Showcase AI tools that let the realtime model persist conversation data into the
/// per-call <see cref="IvrWorkflowState"/>. Any key written through these tools is
/// surfaced automatically in subsequent stages' system prompts under the
/// "Collected Information" section (see <c>RealtimeIvrWorkflowDefinition.BuildContext</c>),
/// so it's the canonical way to teach the agent something now and reuse it later.
/// </summary>
/// <remarks>
/// State is reached via <see cref="ICallSessionAccessor.Current"/> →
/// <see cref="ICallSession.Strategy"/>.<see cref="IConversationStrategy.WorkflowState"/>.
/// When the call is degraded to a non-realtime tier the same state object is preserved
/// by the composite fallback strategy, so values written here survive tier swaps.
/// </remarks>
public static class WorkflowStateTools
{
    /// <summary>
    /// Build the tool the realtime agent calls after asking the caller for their name.
    /// Writes <c>CallerFirstName</c>, <c>CallerLastName</c>, and <c>CallerFullName</c> into
    /// the workflow state so later stages can address the caller by name without
    /// re-prompting.
    /// </summary>
    public static AITool RecordCallerNameTool(ILoggerFactory? loggerFactory = null)
    {
        var logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("RecordCallerName");

        [Description("Record the caller's spoken first and last name into the workflow state so later stages can greet them and personalize responses. Call this exactly once after the caller has clearly stated both names.")]
        RecordCallerNameResult RecordCallerName(
            [Description("The caller's spoken first name (e.g. \"Jordan\").")] string firstName,
            [Description("The caller's spoken last name (e.g. \"Reyes\").")] string lastName,
            IServiceProvider services)
        {
            var first = (firstName ?? string.Empty).Trim();
            var last = (lastName ?? string.Empty).Trim();
            if (first.Length == 0 || last.Length == 0)
            {
                return new RecordCallerNameResult(false, null, null, null, "Both first and last name are required.");
            }

            var state = services.GetService<ICallSessionAccessor>()?.Current?.Strategy.WorkflowState;
            if (state is null)
            {
                logger.LogWarning("RecordCallerName invoked but no active call session is bound to this scope.");
                return new RecordCallerNameResult(false, first, last, null, "No active call session.");
            }

            var fullName = $"{first} {last}";
            state.Set("CallerFirstName", first);
            state.Set("CallerLastName", last);
            state.Set("CallerFullName", fullName);

            logger.LogInformation("Recorded caller name '{FullName}' into workflow state for call {CallId}",
                fullName, services.GetService<ICallSessionAccessor>()?.Current?.CallId ?? "(unknown)");

            return new RecordCallerNameResult(true, first, last, fullName, "Recorded.");
        }

        return AIFunctionFactory.Create((Delegate)RecordCallerName);
    }
}

/// <summary>Envelope returned by <c>record-caller-name</c>.</summary>
public sealed record RecordCallerNameResult(
    bool Success,
    string? FirstName,
    string? LastName,
    string? FullName,
    string Message);
