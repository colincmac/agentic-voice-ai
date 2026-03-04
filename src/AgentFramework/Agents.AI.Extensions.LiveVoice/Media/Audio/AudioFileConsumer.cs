using System;
using System.Collections.Generic;
using System.Text;

namespace Agents.AI.Extensions.LiveVoice.Media.Audio;

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
