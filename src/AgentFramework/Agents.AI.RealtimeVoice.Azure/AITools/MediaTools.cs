using System;
using System.Collections.Generic;
using System.Text;
using Agents.AI.Extensions.AITools;
using Microsoft.Extensions.AI;

namespace Agents.AI.RealtimeVoice.Azure.AITools;

public class MediaTools : IAIToolCollection
{

    public Task<byte[]> TranscribeAudioAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IEnumerable<AITool> AsAITools()
    {
        throw new NotImplementedException();
    }
}
