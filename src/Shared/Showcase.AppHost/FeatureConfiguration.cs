using System;
using System.Collections.Generic;
using System.Text;

namespace Showcase.AppHost;

// WIP feature flags configuration
public class FeatureConfiguration
{
    public const string SectionName = "Features";

    // External Integrations
    public bool EnableBiometricsApi { get; set; } = true;

    // Monitoring & Telemetry
    public bool UseAppInsights { get; set; } = true;

    // Data Stores
    public bool UseRedis { get; set; } = true;
    public bool UseCosmos { get; set; } = true;

    // Communication Channels
    public bool EnableTeams { get; set; } = true;
}
