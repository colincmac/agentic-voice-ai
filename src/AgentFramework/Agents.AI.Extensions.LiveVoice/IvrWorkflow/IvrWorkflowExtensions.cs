//using Agents.AI.Extensions.AITools;
//using Agents.AI.Extensions.LiveVoice.IvrWorkflow.Tools;
//using Microsoft.Extensions.DependencyInjection;
//using Microsoft.Extensions.Logging;

//namespace Agents.AI.Extensions.LiveVoice.IvrWorkflow;

///// <summary>
///// Extension methods for integrating IVR workflows with service collections.
///// </summary>
//public static class IvrWorkflowExtensions
//{
//    /// <summary>
//    /// Adds IVR workflow services to the service collection.
//    /// </summary>
//    public static IServiceCollection AddIvrWorkflowServices(this IServiceCollection services)
//    {
//        services.AddSingleton<IIvrWorkflowSessionFactory, IvrWorkflowSessionFactory>();
//        services.AddSingleton<IIvrOrchestratorFactory, IvrOrchestratorFactory>();
//        services.AddScoped<IAIToolCollection, IvrOrchestratorTools>();
//        return services;
//    }

//    /// <summary>
//    /// Creates a credit card activation workflow (example IVR flow).
//    /// </summary>
//    public static IvrWorkflowDefinition CreateCreditCardActivationWorkflow()
//    {
//        return IvrWorkflowBuilder.Create("CreditCardActivation")
//            .WithWelcomeMessage("Welcome to the credit card activation line.")
//            .WithCompletionMessage("Your card has been activated. Thank you for calling!")
//            .WithFailureMessage("We were unable to complete your request. Please try again or contact customer service.")
//            .AddInputStep(
//                name: "CollectName",
//                voiceAgentInstructions: "To get started, please tell me your full name as it appears on your card.",
//                orchestratorInstructions: "Collect the customer's full name for card activation.",
//                stateKey: "customerName",
//                configure: step => step
//                    .WithNonEmptyValidation("Please provide your full name.")
//                    .WithMaxRetries(3)
//                    .WithRetryPrompt("I didn't catch that. Please say your full name as it appears on your credit card."))
//            .AddInputStep(
//                name: "VoiceEnrollment",
//                prompt: "For your security, we need to verify your identity. Please say a phrase that we can use to recognize your voice in the future. You can say something like 'My voice is my password'.",
//                stateKey: "voicePhrase",
//                configure: step => step
//                    .RequiresPreviousStep("CollectName")
//                    .WithNonEmptyValidation("Please say a phrase for voice enrollment.")
//                    .WithMaxRetries(2))
//            .AddInputStep(
//                name: "CollectLast4",
//                prompt: "Now, please enter or say the last 4 digits of your card number.",
//                stateKey: "last4Digits",
//                configure: step => step
//                    .RequiresPreviousStep("VoiceEnrollment")
//                    .WithPatternValidation(@"^\d{4}$", "Please provide exactly 4 digits.")
//                    .WithInputTransform(input => new string(input.Where(char.IsDigit).ToArray()))
//                    .WithMaxRetries(3)
//                    .WithRetryPrompt("That doesn't look right. Please enter exactly 4 digits from your card."))
//            .AddConfirmationStep(
//                name: "ConfirmActivation",
//                promptBuilder: state =>
//                {
//                    var name = state.Get<string>("customerName");
//                    var last4 = state.Get<string>("last4Digits");
//                    return $"Just to confirm, {name}, you want to activate the card ending in {last4}. Is that correct?";
//                },
//                configure: step => step
//                    .RequiresPreviousStep("CollectLast4")
//                    .JumpToStepOnDeny("CollectName")
//                    .OnConfirm(state => state.Set("activationConfirmed", true)))
//            .Build();
//    }

//    /// <summary>
//    /// Creates a balance inquiry workflow (simple example).
//    /// </summary>
//    public static IvrWorkflowDefinition CreateBalanceInquiryWorkflow()
//    {
//        return IvrWorkflowBuilder.Create("BalanceInquiry")
//            .WithWelcomeMessage("Welcome to the account balance line.")
//            .WithCompletionMessage("Thank you for calling. Have a great day!")
//            .AddInputStep(
//                name: "CollectAccountNumber",
//                prompt: "Please enter or say your 10-digit account number.",
//                stateKey: "accountNumber",
//                configure: step => step
//                    .WithPatternValidation(@"^\d{10}$", "Please provide a valid 10-digit account number.")
//                    .WithInputTransform(input => new string(input.Where(char.IsDigit).ToArray()))
//                    .WithMaxRetries(3))
//            .AddInputStep(
//                name: "CollectPin",
//                prompt: "Please enter your 4-digit PIN.",
//                stateKey: "pin",
//                configure: step => step
//                    .RequiresPreviousStep("CollectAccountNumber")
//                    .WithPatternValidation(@"^\d{4}$", "Please provide exactly 4 digits for your PIN.")
//                    .WithInputTransform(input => new string(input.Where(char.IsDigit).ToArray()))
//                    .WithMaxRetries(3))
//            .AddStep(step => step
//                .WithName("RetrieveBalance")
//                .WithPrompt("Looking up your balance now...")
//                .RequiresPreviousStep("CollectPin")
//                .OnExecute((state, input, ct) =>
//                {
//                    // Simulate balance lookup
//                    var balance = Random.Shared.Next(100, 10000);
//                    state.Set("balance", balance);
//                    return Task.FromResult(IvrStepResult.Succeeded($"Your current balance is ${balance}."));
//                }))
//            .Build();
//    }

//    /// <summary>
//    /// Creates a payment processing workflow.
//    /// </summary>
//    public static IvrWorkflowDefinition CreatePaymentWorkflow()
//    {
//        return IvrWorkflowBuilder.Create("PaymentProcessing")
//            .WithWelcomeMessage("Welcome to the payment center.")
//            .WithCompletionMessage("Your payment has been processed successfully. A confirmation will be sent to your email.")
//            .WithFailureMessage("We were unable to process your payment. Please try again later.")
//            .AddInputStep(
//                name: "CollectPaymentAmount",
//                prompt: "How much would you like to pay today?",
//                stateKey: "paymentAmount",
//                configure: step => step
//                    .WithValidator(new PredicateValidator(
//                        s => decimal.TryParse(s.Get<string>("paymentAmount")?.Replace("$", "").Replace(",", ""), out var amt) && amt > 0,
//                        "Please provide a valid payment amount greater than zero."))
//                    .WithMaxRetries(3))
//            .AddInputStep(
//                name: "CollectPaymentMethod",
//                prompt: "Would you like to pay by checking account or debit card? Say 'checking' or 'debit'.",
//                stateKey: "paymentMethod",
//                configure: step => step
//                    .RequiresPreviousStep("CollectPaymentAmount")
//                    .WithValidator(new PredicateValidator(
//                        s =>
//                        {
//                            var method = s.Get<string>("paymentMethod")?.ToLowerInvariant();
//                            return method?.Contains("check") == true || method?.Contains("debit") == true;
//                        },
//                        "Please say 'checking' or 'debit' for your payment method."))
//                    .WithMaxRetries(3))
//            .AddConfirmationStep(
//                name: "ConfirmPayment",
//                promptBuilder: state =>
//                {
//                    var amount = state.Get<string>("paymentAmount");
//                    var method = state.Get<string>("paymentMethod");
//                    return $"You're about to pay {amount} using your {method}. Should I process this payment?";
//                },
//                configure: step => step
//                    .RequiresPreviousStep("CollectPaymentMethod")
//                    .OnConfirm(state => state.Set("paymentConfirmed", true))
//                    .OnDeny(state => state.Set("paymentCancelled", true)))
//            .Build();
//    }
//}
