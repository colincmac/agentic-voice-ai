using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Showcase.ConsolePlayground;

internal class AzureOpenAISettings
{
    public const string SectionName = "AzureOpenAI";

    public required string Key { get; set; }
    public required string Endpoint { get; set; }
    public required string ChatDeploymentName { get; set; }
    public required string RealtimeDeploymentName { get; set; }

    public bool UseOpenTelemetry { get; set; } = true;
}
