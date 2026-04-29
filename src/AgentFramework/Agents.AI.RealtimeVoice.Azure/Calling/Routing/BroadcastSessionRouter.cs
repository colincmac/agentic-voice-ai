using Agents.AI.Extensions.Helpers.Streaming;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Routing;

/// <summary>
/// Default router: broadcasts audio to all other participants,
/// messages respect <see cref="MessageUpdate.TargetParticipantId"/> for directed delivery.
/// </summary>
public sealed class BroadcastSessionRouter : ISessionRouter
{
    public async Task RouteAudioAsync(
        string sourceParticipantId,
        ReadOnlyMemory<byte> frame,
        IReadOnlyDictionary<string, HubSessionParticipant> participants,
        CancellationToken cancellationToken)
    {
        foreach (var (id, participant) in participants)
        {
            if (id == sourceParticipantId)
            {
                continue;
            }

            try
            {
                await participant.SendAudioAsync(frame, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Individual participant failure should not block routing to others
            }
        }
    }

    public async Task<int> RouteMessageAsync(
        string sourceParticipantId,
        MessageUpdate message,
        IReadOnlyDictionary<string, HubSessionParticipant> participants,
        CancellationToken cancellationToken)
    {
        var targetCount = 0;

        foreach (var (id, participant) in participants)
        {
            if (id == sourceParticipantId)
            {
                continue;
            }

            // Directed messaging: when TargetParticipantId is set, skip non-matching participants
            if (message.TargetParticipantId is not null && id != message.TargetParticipantId)
            {
                continue;
            }

            if (!participant.Metadata.SupportsMessaging)
            {
                continue;
            }

            try
            {
                await participant.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return targetCount;
            }
            catch
            {
                // Individual participant failure should not block routing to others
            }

            targetCount++;
        }

        return targetCount;
    }
}
