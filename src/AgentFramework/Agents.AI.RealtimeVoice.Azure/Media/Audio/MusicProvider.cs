namespace Agents.AI.RealtimeVoice.Azure.Media.Audio;

/// <summary>
/// Provides hold-music or prompt audio from a pre-recorded file, sending
/// frames to the transport via <see cref="IAudioProducer.SendAudioAsync"/>.
/// </summary>
public class MusicProvider(Guid fileId, string fileUri, string fileVersion) : IAudioProducer
{
    public Guid FileId { get; } = fileId;
    public string FileUri { get; } = fileUri;
    public string FileVersion { get; } = fileVersion;

    public Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        // Music playback is outbound-only; this stub satisfies the interface
        // for producers that supply pre-recorded content.
        return Task.CompletedTask;
    }
}
