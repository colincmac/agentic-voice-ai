<#
.SYNOPSIS
    Azure-tenant provisioning for Teams Phone Extensibility (TPE).

.DESCRIPTION
    Runs in the AZURE TENANT context. Performs two phases:

      Phase 1 — Create Azure Bot Service (linked to Entra App from Teams tenant)
      Phase 2 — Authorize ACS to accept calls for the Teams Resource Account (TPE assignment)

    This script depends on outputs from setup_tpe_teams.ps1:
      - Entra App Client ID  (needed for Bot Service creation)
      - RA Object ID         (needed for TPE assignment)
      - Teams Tenant ID      (needed for both)

    These can be supplied via -TeamsOutputFile (JSON from setup_tpe_teams.ps1)
    or as individual parameters.

    Required roles:
      - Contributor on the Azure resource group

    Required CLIs:
      - Azure CLI (az) — authenticated to the Azure tenant

.PARAMETER ConfigFile
    Path to a JSON config file (same schema as tpe-config.sample.json).
    Provides Azure-side parameters (tenant, subscription, RG, ACS, bot name).

.PARAMETER TeamsOutputFile
    Path to the JSON output file from setup_tpe_teams.ps1.
    Provides: entraAppClientId, teamsResourceAccountObjectId, teamsTenantId.

.PARAMETER AzureTenantId
    Directory (tenant) ID of the Azure subscription tenant.

.PARAMETER AzureSubscriptionId
    Azure subscription ID.

.PARAMETER AzureResourceGroupName
    Resource group for Bot Service.

.PARAMETER AcsCommunicationServicesName
    DNS name of the ACS resource (used for TPE assignment API endpoint).

.PARAMETER AzureBotServiceName
    Name for the Azure Bot Service resource.

.PARAMETER EntraAppClientId
    Client ID of the Entra App Registration (from Teams tenant).
    Automatically read from -TeamsOutputFile if provided.

.PARAMETER TeamsTenantId
    Teams tenant ID. Automatically read from -TeamsOutputFile if provided.

.PARAMETER TeamsResourceAccountObjectId
    Object ID of the Teams Resource Account.
    Automatically read from -TeamsOutputFile if provided.

.PARAMETER Phases
    Which phases to run. Default: All.

.EXAMPLE
    # Full run — reads Azure config from file, Teams outputs from setup_tpe_teams.ps1
    .\setup_tpe_azure.ps1 `
        -ConfigFile .\tpe-config.sample.json `
        -TeamsOutputFile .\tpe-teams-output.json

.EXAMPLE
    # Only create the TPE assignment (Phase 2), providing IDs directly
    .\setup_tpe_azure.ps1 `
        -AzureTenantId "16b3c013-..." `
        -AcsCommunicationServicesName "woodgrove-ai" `
        -TeamsTenantId "47391752-..." `
        -TeamsResourceAccountObjectId "748a7c7c-..." `
        -Phases Phase2
#>

[CmdletBinding(DefaultParameterSetName = 'Params')]
param(
    [string] $ConfigFile,
    [string] $TeamsOutputFile,

    [Parameter(ParameterSetName = 'Params')] [string] $AzureTenantId,
    [Parameter(ParameterSetName = 'Params')] [string] $AzureSubscriptionId,
    [Parameter(ParameterSetName = 'Params')] [string] $AzureResourceGroupName,
    [Parameter(ParameterSetName = 'Params')] [string] $AcsCommunicationServicesName,
    [Parameter(ParameterSetName = 'Params')] [string] $AzureBotServiceName,

    # These can come from -TeamsOutputFile or be specified directly
    [string] $EntraAppClientId,
    [string] $TeamsTenantId,
    [string] $TeamsResourceAccountObjectId,

    [ValidateSet('All', 'Phase1', 'Phase2')]
    [string[]] $Phases = @('All'),

    [switch] $SkipBotCreation,
    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'
$TpeApiVersion = "2025-06-30"

#region Load config file (Azure-side parameters)
if ($ConfigFile) {
    $config = Get-Content $ConfigFile -Raw | ConvertFrom-Json

    if (-not $AzureTenantId)                { $AzureTenantId = $config.azure.tenantId }
    if (-not $AzureSubscriptionId)          { $AzureSubscriptionId = $config.azure.subscriptionId }
    if (-not $AzureResourceGroupName)       { $AzureResourceGroupName = $config.azure.resourceGroupName }
    if (-not $AcsCommunicationServicesName) { $AcsCommunicationServicesName = $config.azure.acsName }
    if (-not $AzureBotServiceName)          { $AzureBotServiceName = $config.azure.botServiceName }
}
#endregion

#region Load Teams output file (cross-tenant dependencies)
if ($TeamsOutputFile) {
    if (-not (Test-Path $TeamsOutputFile)) {
        throw "Teams output file not found: $TeamsOutputFile. Run setup_tpe_teams.ps1 first."
    }
    $teamsOutput = Get-Content $TeamsOutputFile -Raw | ConvertFrom-Json

    if (-not $EntraAppClientId)             { $EntraAppClientId = $teamsOutput.entraAppClientId }
    if (-not $TeamsTenantId)                { $TeamsTenantId = $teamsOutput.teamsTenantId }
    if (-not $TeamsResourceAccountObjectId) { $TeamsResourceAccountObjectId = $teamsOutput.teamsResourceAccountObjectId }

    Write-Host "Loaded Teams outputs from: $TeamsOutputFile" -ForegroundColor Cyan
    Write-Host "  Entra App Client ID: $EntraAppClientId"
    Write-Host "  Teams Tenant ID:     $TeamsTenantId"
    Write-Host "  RA Object ID:        $TeamsResourceAccountObjectId"
    Write-Host ""
}
#endregion

function ShouldRunPhase([string]$phase) {
    return ($Phases -contains 'All') -or ($Phases -contains $phase)
}

function Write-Phase([string]$phase, [string]$description) {
    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
    Write-Host "  $phase — $description" -ForegroundColor Green
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
    Write-Host ""
}

#region Phase 1 — Create Azure Bot Service
if (ShouldRunPhase 'Phase1') {
    Write-Phase "Phase 1/2" "Create Azure Bot Service"

    if (-not $EntraAppClientId) {
        throw "Phase 1 requires -EntraAppClientId. Run setup_tpe_teams.ps1 first or provide -TeamsOutputFile."
    }
    if (-not $TeamsTenantId) {
        throw "Phase 1 requires -TeamsTenantId."
    }
    if ($SkipBotCreation) {
        Write-Host "Skipping Bot Service creation (-SkipBotCreation)." -ForegroundColor Yellow
    }
    else {
        Write-Host "Signing in to Azure tenant $AzureTenantId..." -ForegroundColor Cyan
        if (-not $WhatIf) { az login --tenant $AzureTenantId }

        Write-Host "Creating Bot Service '$AzureBotServiceName'..." -ForegroundColor Cyan
        Write-Host "  Resource Group: $AzureResourceGroupName" -ForegroundColor DarkCyan
        Write-Host "  App ID:         $EntraAppClientId (from Teams tenant)" -ForegroundColor DarkCyan
        Write-Host "  Tenant ID:      $TeamsTenantId (Teams tenant)" -ForegroundColor DarkCyan

        $botArgs = @(
            "bot", "create",
            "--resource-group", $AzureResourceGroupName,
            "--name", $AzureBotServiceName,
            "--app-type", "MultiTenant",
            "--appid", $EntraAppClientId,
            "--tenant-id", $TeamsTenantId,
            "--sku", "S1",
            "--location", "global",
            "--subscription", $AzureSubscriptionId
        )

        if ($WhatIf) {
            Write-Host "[WhatIf] az $($botArgs -join ' ')" -ForegroundColor DarkGray
        }
        else {
            az @botArgs
            if ($LASTEXITCODE -ne 0) { throw "Bot Service creation failed (exit code $LASTEXITCODE)." }
            Write-Host "Bot Service created successfully." -ForegroundColor Green
        }
    }
}
#endregion

#region Phase 2 — ACS TPE Authorization
if (ShouldRunPhase 'Phase2') {
    Write-Phase "Phase 2/2" "Authorize ACS to Accept Calls (TPE Assignment)"

    if (-not $TeamsResourceAccountObjectId) {
        throw "Phase 2 requires -TeamsResourceAccountObjectId. Run setup_tpe_teams.ps1 first or provide -TeamsOutputFile."
    }
    if (-not $TeamsTenantId) {
        throw "Phase 2 requires -TeamsTenantId."
    }
    if (-not $AcsCommunicationServicesName) {
        throw "Phase 2 requires -AcsCommunicationServicesName."
    }

    # Ensure Azure login
    $azAccount = az account show 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Signing in to Azure tenant $AzureTenantId..." -ForegroundColor Cyan
        if (-not $WhatIf) { az login --tenant $AzureTenantId }
    }

    $scriptPath = Join-Path $PSScriptRoot "azure_acs_tpe_auth.ps1"
    if (-not (Test-Path $scriptPath)) {
        throw "Required script not found: $scriptPath. Ensure azure_acs_tpe_auth.ps1 is in the same directory."
    }

    Write-Host "Creating TPE assignment..." -ForegroundColor Cyan
    Write-Host "  ACS Resource:  $AcsCommunicationServicesName" -ForegroundColor DarkCyan
    Write-Host "  Teams Tenant:  $TeamsTenantId" -ForegroundColor DarkCyan
    Write-Host "  RA Object ID:  $TeamsResourceAccountObjectId" -ForegroundColor DarkCyan

    if ($WhatIf) {
        Write-Host "[WhatIf] azure_acs_tpe_auth.ps1 -AzureCommunicationServicesName $AcsCommunicationServicesName -TeamsTenantId $TeamsTenantId -TeamsResourceAccountObjectId $TeamsResourceAccountObjectId" -ForegroundColor DarkGray
    }
    else {
        & $scriptPath `
            -AzureCommunicationServicesName $AcsCommunicationServicesName `
            -TeamsTenantId $TeamsTenantId `
            -TeamsResourceAccountObjectId $TeamsResourceAccountObjectId `
            -PrincipalType "teamsResourceAccount" `
            -Verbose

        Write-Host "TPE assignment created." -ForegroundColor Green

        # Verify
        Write-Host "Verifying TPE assignment..." -ForegroundColor Cyan
        $verifyUrl = "https://$AcsCommunicationServicesName.communication.azure.com/access/teamsExtension/tenants/$TeamsTenantId/assignments/$TeamsResourceAccountObjectId`?api-version=$TpeApiVersion"
        $verifyResult = az rest --method GET --url $verifyUrl --resource "https://communication.azure.com" 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Verification successful:" -ForegroundColor Green
            Write-Host $verifyResult
        }
        else {
            Write-Warning "Verification GET returned non-zero exit code. The assignment may still be propagating."
        }
    }
}
#endregion

#region Summary
Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "  Azure Tenant Setup Complete" -ForegroundColor Green
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host ""
Write-Host "  Bot Service:    $AzureBotServiceName"
Write-Host "  ACS Resource:   $AcsCommunicationServicesName"
Write-Host "  RA Object ID:   $TeamsResourceAccountObjectId"
Write-Host "  Teams Tenant:   $TeamsTenantId"
Write-Host ""
Write-Host "The Teams resource account is now authorized to route calls through ACS." -ForegroundColor Green
Write-Host ""
#endregion
