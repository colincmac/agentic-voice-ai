using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace Agents.AI.RealtimeVoice.Azure.CallAutomation;


public class AzureTeamsConfigurationStartupService : IHostedService
{
    private readonly AzureCommunicationService _acsClient;
    public AzureTeamsConfigurationStartupService(AzureCommunicationService acsClient)
    {
        _acsClient = acsClient;
    }
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var response = await _acsClient.AddConfiguredTeamsResourceAccessAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Cleanup logic if needed
        return Task.CompletedTask;
    }
}
