using Azure.Communication;
using Azure.Communication.CallAutomation;

namespace Agents.AI.ContactCenter.Calling.Core;

/// <summary>
/// Shared helpers for the ACS-backed <see cref="ICallControl"/> implementations.
/// </summary>
internal static class AcsCallControl
{
    /// <summary>
    /// Build the strongly typed ACS <see cref="TransferToParticipantOptions"/>
    /// for the given <see cref="TransferRequest"/>. The target identifier shape
    /// is selected based on <see cref="TransferRequest.Kind"/>.
    /// </summary>
    public static TransferToParticipantOptions BuildTransferOptions(TransferRequest request)
    {
        TransferToParticipantOptions options = request.Kind switch
        {
            TransferKind.BlindToPhoneNumber => new TransferToParticipantOptions(
                new PhoneNumberIdentifier(request.TargetIdentifier)),
            TransferKind.BlindToTeamsUser => new TransferToParticipantOptions(
                new MicrosoftTeamsUserIdentifier(request.TargetIdentifier)),
            TransferKind.Consultative => new TransferToParticipantOptions(
                new CommunicationUserIdentifier(request.TargetIdentifier)),
            _ => new TransferToParticipantOptions(
                new CommunicationUserIdentifier(request.TargetIdentifier))
        };

        if (request.CustomContext is { Count: > 0 } context)
        {
            options.OperationContext = string.Join(";", context.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        return options;
    }
}
