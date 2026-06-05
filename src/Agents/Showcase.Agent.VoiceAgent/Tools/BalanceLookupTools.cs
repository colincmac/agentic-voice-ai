using System.ComponentModel;
using System.Globalization;
using Agents.AI.ContactCenter.Authentication;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Showcase.Agent.VoiceAgent.Authentication;

namespace Showcase.Agent.VoiceAgent.Tools;

/// <summary>
/// Demo balance-lookup tool. Gated behind <see cref="CallerVerificationLevel.MultiFactor"/>
/// via <see cref="RequiresCallerVerificationAttribute"/>, so the tool-approval pipeline
/// rejects the call if the caller hasn't completed PIN + SMS-OTP elevation. Reads the
/// mock account from <see cref="InMemoryCallerDirectory"/> claims (<c>balance</c> +
/// <c>balancePending</c>) so each seeded caller returns a deterministic figure.
/// </summary>
public sealed class BalanceLookupTools
{
    /// <summary>Stable tool name used in YAML workflows.</summary>
    public const string LookupBalanceToolName = "lookup-balance";

    private readonly InMemoryCallerDirectory _directory;
    private readonly CallerAuthenticationState _callerAuthenticationState;
    private readonly ILogger<BalanceLookupTools> _logger;

    public BalanceLookupTools(
        InMemoryCallerDirectory directory,
        CallerAuthenticationState callerAuthenticationState,
        ILogger<BalanceLookupTools>? logger = null)
    {
        _directory = directory;
        _callerAuthenticationState = callerAuthenticationState;
        _logger = logger ?? NullLogger<BalanceLookupTools>.Instance;
    }

    [Description("Look up the verified caller's current account balance. Only callable after the caller has completed multi-factor verification. Returns available and pending amounts in USD.")]
    [RequiresCallerVerification(CallerVerificationLevel.MultiFactor, FailureMessage = "Balance access requires multi-factor verification.")]
    public BalanceLookupResult LookupBalance()
    {
        var state = _callerAuthenticationState;
        var identity = state.Identity;

        var record = _directory.FindByUserId(identity.UserId);
        if (record is null)
        {
            _logger.LogWarning("Balance lookup failed: no directory record for {UserId}", identity.UserId);
            return new BalanceLookupResult(false, identity.UserId, identity.DisplayName, null, null, null, null, "No account on file for this caller.");
        }

        var available = TryReadDecimal(record.Claims, "balance");
        var pending = TryReadDecimal(record.Claims, "balancePending");
        var tier = record.Claims.TryGetValue("accountTier", out var tierClaim) ? tierClaim?.ToString() : null;

        if (available is null)
        {
            return new BalanceLookupResult(false, identity.UserId, identity.DisplayName, null, null, "USD", tier, "Balance is not available right now.");
        }

        _logger.LogInformation(
            "Returned balance for {UserId} ({DisplayName}): available={Available} pending={Pending}",
            identity.UserId, identity.DisplayName, available, pending);

        return new BalanceLookupResult(
            Success: true,
            AccountId: identity.UserId,
            AccountHolder: identity.DisplayName,
            AvailableBalance: available,
            PendingBalance: pending ?? 0m,
            Currency: "USD",
            AccountTier: tier,
            Message: "Balance retrieved.");
    }

    /// <summary>Build the <see cref="AIFunction"/> for <see cref="LookupBalance"/> bound to <paramref name="instance"/>.</summary>
    public static AIFunction BuildLookupBalanceTool(BalanceLookupTools instance) =>
        AIFunctionFactory.Create(instance.LookupBalance, name: LookupBalanceToolName);

    private static decimal? TryReadDecimal(IReadOnlyDictionary<string, object?> claims, string key)
    {
        if (!claims.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }
        return raw switch
        {
            decimal d => d,
            double dd => (decimal)dd,
            float ff => (decimal)ff,
            int ii => ii,
            long ll => ll,
            string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }
}

/// <summary>Envelope returned by <c>lookup-balance</c>.</summary>
public sealed record BalanceLookupResult(
    bool Success,
    string AccountId,
    string? AccountHolder,
    decimal? AvailableBalance,
    decimal? PendingBalance,
    string? Currency,
    string? AccountTier,
    string Message);
