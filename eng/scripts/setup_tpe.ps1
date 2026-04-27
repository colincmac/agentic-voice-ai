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

    # --- Azure Tenant ---
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $AzureTenantId,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $AzureSubscriptionId,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $AzureResourceGroupName,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $AcsCommunicationServiceGlobalId,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $AcsCommunicationServicesName,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $AzureBotServiceName,

    # --- Teams Tenant ---
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $TeamsTenantId,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $EntraAppRegistrationName,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $TeamsResourceAccountUpn,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $TeamsResourceAccountDisplayName,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $TeamsPhoneNumber,
    [Parameter(ParameterSetName = 'Params')]
    [ValidateSet('CallingPlan', 'DirectRouting', 'OperatorConnect')]
    [string] $PhoneNumberType = 'CallingPlan',
    [Parameter(ParameterSetName = 'Params')] [string] $TeamsUsageLocation = 'US',

    # --- Existing Entra App (skip Phase 2 if provided) ---
    [string] $ExistingEntraAppClientId,

    # --- Phase selection ---
    [ValidateSet('All', 'Phase1', 'Phase2', 'Phase3', 'Phase4', 'Phase5')]
    [string[]] $Phases = @('All'),

    # --- Behavior ---
    [switch] $SkipBotCreation,
    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'

# Well-known constants
$AcsFirstPartyAppId          = "1fd5118e-2576-4263-8130-9503064c837a"
$TeamsExtManageCallsPermId   = "9ed60762-c537-4e50-8984-4b1db3d922ce"
$TeamsPhoneRASkuId           = "440eaaa8-b3e0-484b-a8be-62870b9ba70a"
$TpeApiVersion               = "2025-06-30"

#region Config file loading
if ($ConfigFile) {
    $config = Get-Content $ConfigFile -Raw | ConvertFrom-Json

    $AzureTenantId                = $config.azure.tenantId
    $AzureSubscriptionId          = $config.azure.subscriptionId
    $AzureResourceGroupName       = $config.azure.resourceGroupName
    $AcsCommunicationServiceGlobalId = $config.azure.acsGlobalId
    $AcsCommunicationServicesName = $config.azure.acsName
    $AzureBotServiceName          = $config.azure.botServiceName

    $TeamsTenantId                = $config.teams.tenantId
    $EntraAppRegistrationName     = $config.teams.entraAppName
    $TeamsResourceAccountUpn      = $config.teams.resourceAccountUpn
    $TeamsResourceAccountDisplayName = $config.teams.resourceAccountDisplayName
    $TeamsPhoneNumber             = $config.teams.phoneNumber
    $PhoneNumberType              = if ($config.teams.phoneNumberType) { $config.teams.phoneNumberType } else { 'CallingPlan' }
    $TeamsUsageLocation           = if ($config.teams.usageLocation) { $config.teams.usageLocation } else { 'US' }

    if ($config.teams.existingEntraAppClientId) {
        $ExistingEntraAppClientId = $config.teams.existingEntraAppClientId
    }
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

# Track outputs across phases
$entraAppClientId = $ExistingEntraAppClientId
$teamsResourceAccountObjectId = $null

#region Phase 1 — Azure: Create Bot Service
if (ShouldRunPhase 'Phase1') {
    Write-Phase "Phase 1" "Azure Tenant — Create Bot Service"

    if (-not $entraAppClientId) {
        Write-Warning "Phase 1 requires the Entra App Client ID. Run Phase 2 first or provide -ExistingEntraAppClientId."
        Write-Warning "Skipping Phase 1."
    }
    elseif ($SkipBotCreation) {
        Write-Host "Skipping Bot Service creation (-SkipBotCreation specified)." -ForegroundColor Yellow
    }
    else {
        Write-Host "Signing in to Azure tenant $AzureTenantId..." -ForegroundColor Cyan
        if (-not $WhatIf) { az login --tenant $AzureTenantId }

        Write-Host "Creating Bot Service '$AzureBotServiceName'..." -ForegroundColor Cyan
        $botArgs = @(
            "bot", "create",
            "--resource-group", $AzureResourceGroupName,
            "--name", $AzureBotServiceName,
            "--app-type", "MultiTenant",
            "--appid", $entraAppClientId,
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
            if ($LASTEXITCODE -ne 0) { throw "Bot Service creation failed." }
            Write-Host "Bot Service created successfully." -ForegroundColor Green
        }
    }
}
#endregion

#region Phase 2 — Teams: Create Entra App Registration
if (ShouldRunPhase 'Phase2') {
    Write-Phase "Phase 2" "Teams Tenant — Create Entra App Registration"

    if ($ExistingEntraAppClientId) {
        Write-Host "Using existing Entra App Client ID: $ExistingEntraAppClientId" -ForegroundColor Yellow
        $entraAppClientId = $ExistingEntraAppClientId
    }
    else {
        Write-Host "Connecting to Teams tenant Entra ID ($TeamsTenantId)..." -ForegroundColor Cyan
        if (-not $WhatIf) {
            Connect-Entra -Scopes "Application.ReadWrite.All", "AppRoleAssignment.ReadWrite.All" -TenantId $TeamsTenantId
        }

        $requiredResourceAccess = @(
            @{
                resourceAppId  = $AcsFirstPartyAppId
                resourceAccess = @(
                    @{
                        id   = $TeamsExtManageCallsPermId
                        type = "Scope"
                    }
                )
            }
        )

        Write-Host "Creating Entra App Registration '$EntraAppRegistrationName'..." -ForegroundColor Cyan
        if ($WhatIf) {
            Write-Host "[WhatIf] New-EntraApplication -DisplayName $EntraAppRegistrationName" -ForegroundColor DarkGray
        }
        else {
            $entraApp = New-EntraApplication -DisplayName $EntraAppRegistrationName -RequiredResourceAccess $requiredResourceAccess
            $entraAppClientId = $entraApp.AppId
            Write-Host "Entra App created. Client ID: $entraAppClientId" -ForegroundColor Green
        }
    }
}
#endregion

#region Phase 3 — Teams: Provision Resource Account
if (ShouldRunPhase 'Phase3') {
    Write-Phase "Phase 3" "Teams Tenant — Provision Resource Account"

    if (-not $entraAppClientId) {
        throw "Phase 3 requires the Entra App Client ID. Run Phase 2 first or provide -ExistingEntraAppClientId."
    }

    Write-Host "Connecting to Microsoft Teams ($TeamsTenantId)..." -ForegroundColor Cyan
    if (-not $WhatIf) {
        Connect-MicrosoftTeams -TenantId $TeamsTenantId
        Connect-Graph -Scopes User.ReadWrite.All, Organization.Read.All
    }

    Write-Host "Creating resource account '$TeamsResourceAccountUpn'..." -ForegroundColor Cyan
    if ($WhatIf) {
        Write-Host "[WhatIf] New-CsOnlineApplicationInstance -UserPrincipalName $TeamsResourceAccountUpn -ApplicationId $entraAppClientId" -ForegroundColor DarkGray
    }
    else {
        $teamsResourceAccount = New-CsOnlineApplicationInstance `
            -UserPrincipalName $TeamsResourceAccountUpn `
            -ApplicationId $entraAppClientId `
            -DisplayName $TeamsResourceAccountDisplayName

        $teamsResourceAccountObjectId = $teamsResourceAccount.ObjectId
        Write-Host "Resource Account created. ObjectId: $teamsResourceAccountObjectId" -ForegroundColor Green

        Write-Host "Linking resource account to ACS resource ($AcsCommunicationServiceGlobalId)..." -ForegroundColor Cyan
        Set-CsOnlineApplicationInstance `
            -Identity $teamsResourceAccount.UserPrincipalName `
            -ApplicationId $entraAppClientId `
            -AcsResourceId $AcsCommunicationServiceGlobalId

        Write-Host "Syncing resource account..." -ForegroundColor Cyan
        Sync-CsOnlineApplicationInstance `
            -ObjectId $teamsResourceAccount.ObjectId `
            -ApplicationId $entraAppClientId

        Write-Host "Resource account provisioned and synced." -ForegroundColor Green
    }
}
#endregion

#region Phase 4 — Teams: License and Assign Phone Number
if (ShouldRunPhase 'Phase4') {
    Write-Phase "Phase 4" "Teams Tenant — License and Assign Phone Number"

    $raUpn = $TeamsResourceAccountUpn

    Write-Host "Waiting for resource account to appear in Entra ID..." -ForegroundColor Cyan
    if (-not $WhatIf) {
        $retryCount = 0
        $maxRetries = 20
        do {
            $retryCount++
            Write-Host "  Attempt $retryCount/$maxRetries — checking Entra for $raUpn..." -ForegroundColor DarkCyan
            Start-Sleep 15
            try {
                $resourceAccountObject = Get-MgUser -UserId $raUpn -ErrorAction Stop
            }
            catch {
                $resourceAccountObject = $null
            }
        } until (($resourceAccountObject -and $resourceAccountObject.UserPrincipalName -eq $raUpn) -or ($retryCount -ge $maxRetries))

        if (-not $resourceAccountObject) {
            throw "Resource account $raUpn did not appear in Entra ID after $maxRetries attempts."
        }
        Write-Host "Resource account found in Entra ID." -ForegroundColor Green

        Write-Host "Setting usage location to '$TeamsUsageLocation'..." -ForegroundColor Cyan
        Update-MgUser -UserId $raUpn -UsageLocation $TeamsUsageLocation
        Start-Sleep 15

        Write-Host "Assigning Teams Phone Resource Account license..." -ForegroundColor Cyan
        $licenseRetry = 0
        do {
            $error.Clear()
            $licenseRetry++
            Start-Sleep 15
            try {
                Set-MgUserLicense -UserId $raUpn `
                    -AddLicenses @(@{SkuId = $TeamsPhoneRASkuId}) `
                    -RemoveLicenses @()
            }
            catch {
                Write-Host "  License assignment attempt $licenseRetry failed, retrying..." -ForegroundColor Yellow
            }
        } until ((!$error) -or ($licenseRetry -ge 10))

        if ($error) { throw "License assignment failed after $licenseRetry attempts." }
        Write-Host "License assigned." -ForegroundColor Green

        Write-Host "Assigning phone number $TeamsPhoneNumber ($PhoneNumberType)..." -ForegroundColor Cyan
        Set-CsPhoneNumberAssignment `
            -Identity $raUpn `
            -PhoneNumber $TeamsPhoneNumber `
            -PhoneNumberType $PhoneNumberType
        Write-Host "Phone number assigned." -ForegroundColor Green
    }
    else {
        Write-Host "[WhatIf] Would assign license and phone number $TeamsPhoneNumber to $raUpn" -ForegroundColor DarkGray
    }
}
#endregion

#region Phase 5 — Azure: ACS TPE Authorization
if (ShouldRunPhase 'Phase5') {
    Write-Phase "Phase 5" "Azure Tenant — Authorize ACS to Accept Calls (TPE Assignment)"

    # Resolve the RA ObjectId if we don't have it from Phase 3
    if (-not $teamsResourceAccountObjectId) {
        Write-Host "Looking up resource account ObjectId for $TeamsResourceAccountUpn..." -ForegroundColor Cyan
        if (-not $WhatIf) {
            $raInstance = Get-CsOnlineApplicationInstance -Identity $TeamsResourceAccountUpn
            $teamsResourceAccountObjectId = $raInstance.ObjectId
            Write-Host "Found ObjectId: $teamsResourceAccountObjectId" -ForegroundColor Green
        }
    }

    $scriptPath = Join-Path $PSScriptRoot "azure_acs_tpe_auth.ps1"
    if (-not (Test-Path $scriptPath)) {
        throw "Required script not found: $scriptPath. Ensure azure_acs_tpe_auth.ps1 is in the same directory."
    }

    Write-Host "Calling azure_acs_tpe_auth.ps1 to create TPE assignment..." -ForegroundColor Cyan
    if ($WhatIf) {
        Write-Host "[WhatIf] .\azure_acs_tpe_auth.ps1 -AzureCommunicationServicesName $AcsCommunicationServicesName -TeamsTenantId $TeamsTenantId -TeamsResourceAccountObjectId $teamsResourceAccountObjectId" -ForegroundColor DarkGray
    }
    else {
        & $scriptPath `
            -AzureCommunicationServicesName $AcsCommunicationServicesName `
            -TeamsTenantId $TeamsTenantId `
            -TeamsResourceAccountObjectId $teamsResourceAccountObjectId `
            -PrincipalType "teamsResourceAccount" `
            -Verbose

        Write-Host "TPE assignment created successfully." -ForegroundColor Green

        # Verify
        Write-Host "Verifying TPE assignment..." -ForegroundColor Cyan
        $verifyUrl = "https://$AcsCommunicationServicesName.communication.azure.com/access/teamsExtension/tenants/$TeamsTenantId/assignments/$teamsResourceAccountObjectId`?api-version=$TpeApiVersion"
        az rest --method GET --url $verifyUrl --resource "https://communication.azure.com"
    }
}
#endregion

#region Summary
Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "  Setup Complete" -ForegroundColor Green
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host ""
Write-Host "  Entra App Client ID:       $entraAppClientId"
Write-Host "  Resource Account UPN:      $TeamsResourceAccountUpn"
Write-Host "  Resource Account ObjectId: $teamsResourceAccountObjectId"
Write-Host "  Phone Number:              $TeamsPhoneNumber"
Write-Host "  ACS Resource:              $AcsCommunicationServicesName"
Write-Host "  Bot Service:               $AzureBotServiceName"
Write-Host ""
#endregion
