using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Showcase.ConsolePlayground;

public interface IAudioListener
{
    public Task StopAudioAsync(CancellationToken cancellationToken = default);

    public Task SendAudioAsync(Microsoft.Extensions.AI.DataContent audioEvent, CancellationToken cancellationToken = default);
}
