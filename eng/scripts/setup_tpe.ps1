<#
.SYNOPSIS
    Orchestrator for end-to-end Teams Phone Extensibility (TPE) provisioning.

.DESCRIPTION
    Runs setup_tpe_teams.ps1 (Teams tenant) then setup_tpe_azure.ps1 (Azure tenant)
    in the correct dependency order. The Teams script outputs a JSON file that the
    Azure script consumes.

    For environments where different admins manage each tenant, run the scripts
    individually instead:

      1. Teams Admin runs:  .\setup_tpe_teams.ps1 -ConfigFile .\tpe-config.sample.json
         → produces tpe-teams-output.json

      2. Azure Admin runs:  .\setup_tpe_azure.ps1 -ConfigFile .\tpe-config.sample.json `
                                -TeamsOutputFile .\tpe-teams-output.json

    See docs/tpe-onboarding-guide.md for full documentation.

.PARAMETER ConfigFile
    Path to a JSON configuration file (same schema as tpe-config.sample.json).

.PARAMETER ExistingEntraAppClientId
    If the Entra App already exists, skip its creation and use this Client ID.

.PARAMETER SkipBotCreation
    Skip Azure Bot Service creation (if it already exists).

.PARAMETER WhatIf
    Dry-run mode — prints what would happen without making changes.

.EXAMPLE
    .\setup_tpe.ps1 -ConfigFile .\tpe-config.sample.json

.EXAMPLE
    .\setup_tpe.ps1 -ConfigFile .\tpe-config.sample.json -ExistingEntraAppClientId "10ec1b27-..."

.NOTES
    Required modules: MicrosoftTeams (>=7.5.0), Microsoft.Entra (>=1.2.0), Microsoft.Graph.Users.Actions
    Required CLIs: Azure CLI (az)
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ConfigFile,

    [string] $ExistingEntraAppClientId,
    [switch] $SkipBotCreation,
    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'

$teamsOutputFile = Join-Path $PSScriptRoot "tpe-teams-output.json"

#region Step 1 — Teams Tenant
Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host "  Step 1 of 2 — Teams Tenant Provisioning" -ForegroundColor Magenta
Write-Host "  (Entra App → Resource Account → License + Phone Number)" -ForegroundColor Magenta
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host ""

$teamsArgs = @(
    "-ConfigFile", $ConfigFile,
    "-OutputFile", $teamsOutputFile
)
if ($ExistingEntraAppClientId) { $teamsArgs += "-ExistingEntraAppClientId", $ExistingEntraAppClientId }
if ($WhatIf) { $teamsArgs += "-WhatIf" }

$teamsScript = Join-Path $PSScriptRoot "setup_tpe_teams.ps1"
& $teamsScript @teamsArgs

if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Teams tenant setup failed." }
#endregion

#region Step 2 — Azure Tenant
Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host "  Step 2 of 2 — Azure Tenant Provisioning" -ForegroundColor Magenta
Write-Host "  (Bot Service → ACS TPE Authorization)" -ForegroundColor Magenta
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host ""

$azureArgs = @(
    "-ConfigFile", $ConfigFile,
    "-TeamsOutputFile", $teamsOutputFile
)
if ($SkipBotCreation) { $azureArgs += "-SkipBotCreation" }
if ($WhatIf) { $azureArgs += "-WhatIf" }

$azureScript = Join-Path $PSScriptRoot "setup_tpe_azure.ps1"
& $azureScript @azureArgs

if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Azure tenant setup failed." }
#endregion

Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "  End-to-End TPE Setup Complete" -ForegroundColor Green
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host ""
Write-Host "Teams output saved to: $teamsOutputFile" -ForegroundColor Cyan
Write-Host ""
