[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $AzureCommunicationServicesName,
    [Parameter(Mandatory)] [string] $TeamsTenantId,
    [Parameter(Mandatory)] [string] $TeamsResourceAccountObjectId,
    [ValidateSet('teamsResourceAccount','user')]
    [string] $PrincipalType = 'teamsResourceAccount',
    [string[]] $ClientIds,
    [string] $ApiVersion = '2025-06-30'
)

$ErrorActionPreference = 'Stop'

# Resource endpoint (matches AcsConnectionString.Endpoint in AzureCommunicationService.cs)
$endpoint = "https://$AzureCommunicationServicesName.communication.azure.com"
$url      = "$endpoint/access/teamsExtension/tenants/$TeamsTenantId/assignments/$TeamsResourceAccountObjectId" +
            "?api-version=$ApiVersion"

# Entra-auth variant of the API expects the DTO wrapped in a "request" envelope.
# Inner shape mirrors TeamsExtensionAssignmentCreateOrUpdateRequest
# (principalType + always-present clientIds array).
$payload = [ordered]@{
    request = [ordered]@{
        principalType = $PrincipalType
        clientIds     = @($ClientIds)   # serialize as [] when empty
    }
}
$bodyJson = $payload | ConvertTo-Json -Depth 5 -Compress

# Use a body file so az.cmd doesn't strip the embedded double quotes on Windows
$bodyFile = New-TemporaryFile
try {
    Set-Content -Path $bodyFile -Value $bodyJson -Encoding utf8 -NoNewline

    Write-Verbose "PUT $url"
    Write-Verbose "Body: $bodyJson"

    az rest `
        --method PUT `
        --url $url `
        --resource "https://communication.azure.com" `
        --headers "Content-Type=application/json" `
        --body "@$($bodyFile.FullName)"
}
finally {
    Remove-Item $bodyFile -ErrorAction SilentlyContinue
}
