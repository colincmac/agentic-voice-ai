using System.ComponentModel;
using Agents.AI.Extensions.AITools;
using Microsoft.Extensions.AI;

namespace Showcase.Agent.VoiceAgent;

public class WoodgroveDisputeTools : IAIToolCollection
{
    [Description("Look up a customer account by the last 4 digits of their card and date of birth. Returns account details if verified.")]
    public Task<AccountLookupResult> LookupAccountAsync(
        [Description("Last 4 digits of the customer's credit card")] string lastFourDigits,
        [Description("Customer's date of birth in MM/DD/YYYY format")] string dateOfBirth,
        CancellationToken token = default)
    {
        // Stub: simulate successful verification for test data
        if (lastFourDigits == "4567" && dateOfBirth == "08/11/1987")
        {
            return Task.FromResult(new AccountLookupResult(
                IsVerified: true,
                AccountId: "WG-98765432",
                CustomerName: "Colin McCullough",
                CardType: "Woodgrove Rewards Visa"));
        }

        return Task.FromResult(new AccountLookupResult(
            IsVerified: false,
            AccountId: null,
            CustomerName: null,
            CardType: null));
    }

    [Description("Retrieve recent transactions for a verified account. Returns the last 10 transactions.")]
    public Task<IReadOnlyList<TransactionRecord>> GetRecentTransactionsAsync(
        [Description("The verified account ID")] string accountId,
        CancellationToken token = default)
    {
        // Stub: return sample transactions
        var transactions = new List<TransactionRecord>
        {
            new("TXN-001", "CoffeeShop Express", 5.75m, new DateOnly(2026, 1, 25), "Pending"),
            new("TXN-002", "Online Electronics Store", 249.99m, new DateOnly(2026, 1, 22), "Posted"),
            new("TXN-003", "Gas Station #1234", 48.50m, new DateOnly(2026, 1, 20), "Posted"),
            new("TXN-004", "Suspicious Merchant XYZ", 150.00m, new DateOnly(2026, 1, 18), "Posted"),
            new("TXN-005", "Grocery Mart", 87.32m, new DateOnly(2026, 1, 15), "Posted")
        };

        return Task.FromResult<IReadOnlyList<TransactionRecord>>(transactions);
    }

    [Description("Submit a dispute for a specific transaction. Returns a dispute reference number. Make sure the user is ready to write down the reference number before providing it back to the user.")]
    public Task<DisputeSubmissionResult> SubmitDisputeAsync(
        [Description("The verified account ID")] string accountId,
        [Description("The transaction ID to dispute")] string transactionId,
        [Description("The reason for the dispute: unauthorized, duplicate, incorrect_amount, merchandise_not_received, service_not_provided, or other")] string disputeReason,
        [Description("Optional additional details about the dispute")] string? additionalDetails = null,
        CancellationToken token = default)
    {
        // Stub: simulate successful dispute submission
        var referenceNumber = $"D-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(100000, 999999)}";

        return Task.FromResult(new DisputeSubmissionResult(
            IsSuccess: true,
            ReferenceNumber: referenceNumber,
            ProvisionalCreditDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)),
            EstimatedResolutionDays: 60));
    }

    [Description("Check the status of an existing dispute by reference number.")]
    public Task<DisputeStatusResult> CheckDisputeStatusAsync(
        [Description("The dispute reference number")] string referenceNumber,
        CancellationToken token = default)
    {
        // Stub: return sample dispute status
        return Task.FromResult(new DisputeStatusResult(
            ReferenceNumber: referenceNumber,
            Status: "Under Investigation",
            ProvisionalCreditApplied: true,
            ProvisionalCreditAmount: 150.00m,
            LastUpdated: DateTime.UtcNow.AddDays(-2),
            EstimatedCompletionDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(45))));
    }

    [Description("Transfer the customer to a human dispute specialist.")]
    public Task<string> TransferToSpecialistAsync(
        [Description("Brief reason for the transfer")] string reason,
        CancellationToken token = default)
    {
        return Task.FromResult($"Transferring to a Woodgrove dispute specialist. Reason: {reason}");
    }

    public IEnumerable<AITool> AsAITools()
    {
        yield return AIFunctionFactory.Create(LookupAccountAsync, name: "lookup_account");
        yield return AIFunctionFactory.Create(GetRecentTransactionsAsync, name: "get_recent_transactions");
        yield return AIFunctionFactory.Create(SubmitDisputeAsync, name: "submit_dispute");
        yield return AIFunctionFactory.Create(CheckDisputeStatusAsync, name: "check_dispute_status");
        yield return AIFunctionFactory.Create(TransferToSpecialistAsync, name: "transfer_to_specialist");
    }
}

public record AccountLookupResult(
    bool IsVerified,
    string? AccountId,
    string? CustomerName,
    string? CardType);

public record TransactionRecord(
    string TransactionId,
    string MerchantName,
    decimal Amount,
    DateOnly TransactionDate,
    string Status);

public record DisputeSubmissionResult(
    bool IsSuccess,
    string ReferenceNumber,
    DateOnly ProvisionalCreditDate,
    int EstimatedResolutionDays);

public record DisputeStatusResult(
    string ReferenceNumber,
    string Status,
    bool ProvisionalCreditApplied,
    decimal ProvisionalCreditAmount,
    DateTime LastUpdated,
    DateOnly EstimatedCompletionDate);
