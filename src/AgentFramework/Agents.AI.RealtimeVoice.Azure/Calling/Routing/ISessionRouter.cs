using Agents.AI.Extensions.Helpers.Streaming;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Routing;

/// <summary>
/// Strategy that decides how audio and messages are distributed between participants.
/// Implementations can support broadcast, selective, conference, or hold topologies.
/// </summary>
public interface ISessionRouter
{
    /// <summary>
    /// Routes an audio frame from a source participant to the appropriate targets.
    /// </summary>
    /// <param name="sourceParticipantId">The participant that produced the audio.</param>
    /// <param name="frame">Raw audio frame data.</param>
    /// <param name="participants">All participants in the session.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    Task RouteAudioAsync(
        string sourceParticipantId,
        ReadOnlyMemory<byte> frame,
        IReadOnlyDictionary<string, HubSessionParticipant> participants,
        CancellationToken cancellationToken);

    /// <summary>
    /// Routes a message from a source participant to the appropriate targets.
    /// </summary>
    /// <param name="sourceParticipantId">The participant that sent the message.</param>
    /// <param name="message">The message to route.</param>
    /// <param name="participants">All participants in the session.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>Number of participants the message was routed to.</returns>
    Task<int> RouteMessageAsync(
        string sourceParticipantId,
        MessageUpdate message,
        IReadOnlyDictionary<string, HubSessionParticipant> participants,
        CancellationToken cancellationToken);
}
