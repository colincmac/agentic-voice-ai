using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.IvrWorkflow.Registry;

/// <summary>
/// Built-in tools every IVR workflow can rely on: hand-off to a human agent, end the
/// session, and a no-op acknowledgment that lets workflows transition without invoking
/// real business logic.
/// </summary>
public static class IvrBuiltInTools
{
    public const string TransferToHumanName = "transfer-to-human";
    public const string EndSessionName = "end-session";
    public const string AcknowledgeName = "acknowledge";

    /// <summary>Registers the built-in tool set on the supplied registry.</summary>
    public static IIvrToolRegistry AddBuiltIns(this IIvrToolRegistry registry, JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddTool(TransferToHumanName, CreateTransferToHuman(jsonOptions));
        registry.AddTool(EndSessionName, CreateEndSession(jsonOptions));
        registry.AddTool(AcknowledgeName, CreateAcknowledge(jsonOptions));
        return registry;
    }

    private static AIFunction CreateTransferToHuman(JsonSerializerOptions? jsonOptions) =>
        AIFunctionFactory.Create(
            ([Description("Reason for escalating to a human agent")] string reason) =>
                Task.FromResult($"Transfer requested: {reason}"),
            name: TransferToHumanName,
            description: "Transfer the call to a human agent.",
            serializerOptions: jsonOptions);

    private static AIFunction CreateEndSession(JsonSerializerOptions? jsonOptions) =>
        AIFunctionFactory.Create(
            ([Description("Short summary of how the request was resolved")] string resolution) =>
                Task.FromResult($"Session ended: {resolution}"),
            name: EndSessionName,
            description: "End the current IVR session gracefully.",
            serializerOptions: jsonOptions);

    private static AIFunction CreateAcknowledge(JsonSerializerOptions? jsonOptions) =>
        AIFunctionFactory.Create(
            ([Description("Free-form acknowledgement note")] string note) =>
                Task.FromResult($"Acknowledged: {note}"),
            name: AcknowledgeName,
            description: "Acknowledge a user request without performing additional work.",
            serializerOptions: jsonOptions);
}
