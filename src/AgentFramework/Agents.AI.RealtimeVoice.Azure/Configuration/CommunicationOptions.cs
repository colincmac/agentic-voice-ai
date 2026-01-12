namespace Agents.AI.RealtimeVoice.Azure.Configuration;

public class CommunicationOptions
{
    public const string SectionName = "Communication";
    public bool EnableOpenTelemetryInstrumentation { get; set; } = true;
    public bool ConfigureAcsTeamsIntegration { get; set; } = false;
    public required AcsOptions Acs { get; set; }
    public required TeamsOptions Teams { get; set; }
    public AzureBotOptions? BotOptions { get; set; }
    public ContactCenterOptions ContactCenterOptions { get; set; } = new ContactCenterOptions();
}

public class AcsOptions
{
    public required string ConnectionString { get; set; }
    public required Uri CallBackUri { get; set; }
    public required Uri MediaStreamingUri { get; set; }
    public Uri AcsResourceEndpoint => new(ConnectionString.Split(';').First(s => s.StartsWith("endpoint=", StringComparison.OrdinalIgnoreCase)).Split('=')[1]);
    public string AcsApiVersion { get; set; } = "2025-06-30";
};

public class TeamsOptions
{
    public required string ResourceTenantId { get; set; }
    public required string ResourceObjectId { get; set; }
    //public required string ClientSecret { get; set; }
    //public required string BotId { get; set; }
    //public required string BotDisplayName { get; set; }
    //public required string BotEndpoint { get; set; }
    //public required string AppIdUri { get; set; }
    //public string Authority => $"https://login.microsoftonline.com/{TenantId}";
}
public class TeamsCallQueue
{
    public string? FriendlyName { get; set; }
    public string? Descriptions { get; set; }
    public required string ResourceObjectId { get; set; }
    public required string ResourceAccountPhoneNumber { get; set; }
}

public class AzureBotOptions
{
    public string BotAppId { get; set; } = string.Empty;
    public string BotAppSecret { get; set; } = string.Empty;
    public string BotAppType { get; set; } = string.Empty;
    public string BotTenantId { get; set; } = string.Empty;
}
