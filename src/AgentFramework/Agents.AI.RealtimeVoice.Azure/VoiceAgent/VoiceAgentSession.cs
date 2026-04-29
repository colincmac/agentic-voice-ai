using System;
using System.Collections.Generic;
using System.Text;

namespace Agents.AI.RealtimeVoice.Azure.VoiceAgent;

public class VoiceAgentSession
{
    public string[] PartitionKey => ["Id"];
    public int TimeToLiveInSeconds => 86400;
}

