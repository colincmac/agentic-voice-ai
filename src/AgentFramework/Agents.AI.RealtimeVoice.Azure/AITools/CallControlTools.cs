using System.ComponentModel;
using Agents.AI.Extensions.AITools;
using Agents.AI.RealtimeVoice.Azure.Calling.Proposed;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.RealtimeVoice.Azure.AITools;

/// <summary>
/// AI-callable call-control verbs (hang up, transfer) bound to the live
/// <see cref="ICallSession"/>. The agent reaches the active session through
/// the scoped <see cref="ICallSessionAccessor"/>, so this collection only
/// works when registered inside the per-call DI scope created by
/// <c>CallSessionFactory</c>.
/// </summary>
public sealed class CallControlTools : IAIToolCollection
{
    private readonly ICallSessionAccessor _sessionAccessor;
    private readonly ILogger<CallControlTools> _logger;

    public CallControlTools(
        ICallSessionAccessor sessionAccessor,
        ILogger<CallControlTools>? logger = null)
    {
        _sessionAccessor = sessionAccessor;
        _logger = logger ?? NullLogger<CallControlTools>.Instance;
    }

    [Description(
        "End the current phone call. Use this only when the conversation is complete, " +
        "the caller has confirmed they are done, or escalation is no longer possible. " +
        "After calling this, no further audio will be played to the caller.")]
    public async Task<CallControlResult> HangUpCallAsync(
        [Description("Brief human-readable reason for hanging up (e.g. 'caller satisfied', 'task complete').")]
        string reason,
        CancellationToken cancellationToken = default)
    {
        var session = _sessionAccessor.Current;
        if (session is null)
        {
            _logger.LogWarning("HangUpCallAsync invoked but no active call session is bound to this scope");
            return new CallControlResult(false, "No active call session is bound to this agent.");
        }

        try
        {
            await session.HangUpAsync(hangUpForEveryone: true, reason: reason, cancellationToken).ConfigureAwait(false);
            return new CallControlResult(true, $"Call {session.CallId} hung up.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hang up failed for call {CallId}", session.CallId);
            return new CallControlResult(false, $"Hang up failed: {ex.Message}");
        }
    }

    [Description(
        "Transfer the current phone call to a human or another endpoint. " +
        "Use this when the caller explicitly asks for a person, when the request is " +
        "outside the agent's authorized scope, or when policy requires escalation.")]
    public async Task<CallControlResult> TransferCallAsync(
        [Description("The destination identifier. For 'phone' use E.164 (e.g. '+15551234567'). For 'teams' use the Microsoft Teams user ID. For 'consultative' use an ACS user ID.")]
        string targetIdentifier,
        [Description("Transfer kind: 'phone' (blind transfer to PSTN), 'teams' (blind transfer to Teams user), or 'consultative' (warm transfer to an ACS user).")]
        string transferKind = "phone",
        [Description("Optional reason for the transfer; recorded in telemetry and may be passed as transfer context.")]
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var session = _sessionAccessor.Current;
        if (session is null)
        {
            _logger.LogWarning("TransferCallAsync invoked but no active call session is bound to this scope");
            return new CallControlResult(false, "No active call session is bound to this agent.");
        }

        if (string.IsNullOrWhiteSpace(targetIdentifier))
        {
            return new CallControlResult(false, "targetIdentifier is required.");
        }

        if (!TryParseTransferKind(transferKind, out var kind))
        {
            return new CallControlResult(false,
                $"Unknown transferKind '{transferKind}'. Use 'phone', 'teams', or 'consultative'.");
        }

        var customContext = string.IsNullOrWhiteSpace(reason)
            ? null
            : new Dictionary<string, string> { ["reason"] = reason };

        var request = new TransferRequest(targetIdentifier, kind, customContext);

        try
        {
            await session.TransferAsync(request, cancellationToken).ConfigureAwait(false);
            return new CallControlResult(true,
                $"Transfer initiated for call {session.CallId} to {targetIdentifier} ({kind}).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transfer failed for call {CallId}", session.CallId);
            return new CallControlResult(false, $"Transfer failed: {ex.Message}");
        }
    }

    public IEnumerable<AITool> AsAITools()
    {
        yield return AIFunctionFactory.Create(HangUpCallAsync, name: "hang_up_call");
        yield return AIFunctionFactory.Create(TransferCallAsync, name: "transfer_call");
    }

    private static bool TryParseTransferKind(string raw, out TransferKind kind)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "phone":
            case "pstn":
            case "blind_phone":
            case "blindtophonenumber":
                kind = TransferKind.BlindToPhoneNumber;
                return true;
            case "teams":
            case "blind_teams":
            case "blindtoteamsuser":
                kind = TransferKind.BlindToTeamsUser;
                return true;
            case "consultative":
            case "warm":
                kind = TransferKind.Consultative;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}

/// <summary>Result envelope returned by <see cref="CallControlTools"/> verbs.</summary>
public sealed record CallControlResult(bool Success, string Message);
