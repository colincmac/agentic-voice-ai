using System;
using System.Collections.Generic;
using System.Text;
using Agents.AI.Extensions.LiveVoice.Media.Audio;

namespace Agents.AI.RealtimeVoice.Azure.Media.Audio;

public class AudioFileConsumer : IAudioConsumer
{
    public Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task DumpAudioDataContentAsync(Guid fileId, string fileUri, string fileVersion, ReadOnlyMemory<byte> audioData )
    {

    }

}
