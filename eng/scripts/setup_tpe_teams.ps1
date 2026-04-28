<#
.SYNOPSIS
    Teams-tenant provisioning for Teams Phone Extensibility (TPE).

.DESCRIPTION
    Runs in the TEAMS TENANT context. Performs three phases in dependency order:

      Phase 1 — Create Entra App Registration (with ACS TPE permission)
      Phase 2 — Provision Teams Resource Account (linked to ACS)
      Phase 3 — License the Resource Account and assign a phone number

    Outputs a JSON fragment with the Entra App Client ID and RA Object ID
    required by the Azure-side script (setup_tpe_azure.ps1).

    Required roles:
      - Global Admin  OR  (Application Administrator + Skype for Business Administrator + User Administrator)
      - Microsoft Graph scopes: Application.ReadWrite.All, AppRoleAssignment.ReadWrite.All,
        User.ReadWrite.All, Organization.Read.All

    Required modules:
      - Microsoft.Entra  >= 1.2.0
      - MicrosoftTeams   >= 7.5.0
      - Microsoft.Graph.Users.Actions

.PARAMETER ConfigFile
    Path to a JSON config file (same schema as tpe-config.sample.json).

.PARAMETER TeamsTenantId
    Directory (tenant) ID of the Microsoft 365 / Teams tenant.

.PARAMETER EntraAppRegistrationName
    Display name for the Entra App Registration.

.PARAMETER TeamsResourceAccountUpn
    UPN for the Teams Resource Account (e.g. ivr@contoso.com).

.PARAMETER TeamsResourceAccountDisplayName
    Display name for the Teams Resource Account.

.PARAMETER AcsCommunicationServiceGlobalId
    Immutable resource ID of the Azure Communication Services resource.

.PARAMETER TeamsPhoneNumber
    Phone number to assign (e.g. +16105188952).

.PARAMETER PhoneNumberType
    One of CallingPlan, DirectRouting, OperatorConnect. Default: CallingPlan.

.PARAMETER TeamsUsageLocation
    ISO country code for license assignment. Default: US.

.PARAMETER ExistingEntraAppClientId
    If the Entra App already exists, provide its Client ID to skip Phase 1.

.PARAMETER Phases
    Which phases to run. Default: All. Use to re-run individual phases.

.PARAMETER OutputFile
    Path to write the JSON output containing IDs for the Azure-side script.
    Default: tpe-teams-output.json in the current directory.

.EXAMPLE
    # Full run from config file
    .\setup_tpe_teams.ps1 -ConfigFile .\tpe-config.sample.json

.EXAMPLE
    # Re-run only licensing (Phase 3), Entra App already exists
    .\setup_tpe_teams.ps1 -ConfigFile .\tpe-config.sample.json `
        -ExistingEntraAppClientId "10ec1b27-38db-4fc0-a3de-cc4ec20a9661" `
        -Phases Phase3

.OUTPUTS
    Writes a JSON file (default: tpe-teams-output.json) with:
      entraAppClientId         — needed by setup_tpe_azure.ps1 for Bot creation
      teamsResourceAccountObjectId — needed by setup_tpe_azure.ps1 for TPE assignment
      teamsResourceAccountUpn
      teamsTenantId
#>

[CmdletBinding(DefaultParameterSetName = 'Params')]
param(
    [Parameter(ParameterSetName = 'ConfigFile', Mandatory)]
    [string] $ConfigFile,

    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $TeamsTenantId,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $EntraAppRegistrationName,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $TeamsResourceAccountUpn,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $TeamsResourceAccountDisplayName,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $AcsCommunicationServiceGlobalId,
    [Parameter(ParameterSetName = 'Params', Mandatory)] [string] $TeamsPhoneNumber,
    [Parameter(ParameterSetName = 'Params')]
    [ValidateSet('CallingPlan', 'DirectRouting', 'OperatorConnect')]
    [string] $PhoneNumberType = 'CallingPlan',
    [Parameter(ParameterSetName = 'Params')] [string] $TeamsUsageLocation = 'US',

    [string] $ExistingEntraAppClientId,

    [ValidateSet('All', 'Phase1', 'Phase2', 'Phase3')]
    [string[]] $Phases = @('All'),

    [string] $OutputFile = (Join-Path $PWD "tpe-teams-output.json"),

    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'

# Well-known constants
$AcsFirstPartyAppId        = "1fd5118e-2576-4263-8130-9503064c837a"
$TeamsExtManageCallsPermId = "9ed60762-c537-4e50-8984-4b1db3d922ce"
$TeamsPhoneRASkuId         = "440eaaa8-b3e0-484b-a8be-62870b9ba70a"

#region Config file loading
if ($ConfigFile) {
    $config = Get-Content $ConfigFile -Raw | ConvertFrom-Json

    $TeamsTenantId                   = $config.teams.tenantId
    $EntraAppRegistrationName        = $config.teams.entraAppName
    $TeamsResourceAccountUpn         = $config.teams.resourceAccountUpn
    $TeamsResourceAccountDisplayName = $config.teams.resourceAccountDisplayName
    $AcsCommunicationServiceGlobalId = $config.azure.acsGlobalId
    $TeamsPhoneNumber                = $config.teams.phoneNumber
    $PhoneNumberType                 = if ($config.teams.phoneNumberType) { $config.teams.phoneNumberType } else { 'CallingPlan' }
    $TeamsUsageLocation              = if ($config.teams.usageLocation) { $config.teams.usageLocation } else { 'US' }

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

# Track outputs
$entraAppClientId           = $ExistingEntraAppClientId
$teamsResourceAccountObjectId = $null

#region Phase 1 — Create Entra App Registration
if (ShouldRunPhase 'Phase1') {
    Write-Phase "Phase 1/3" "Create Entra App Registration"

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
        Write-Host "  Permission: TeamsExtension.ManageCalls ($TeamsExtManageCallsPermId)" -ForegroundColor DarkCyan
        Write-Host "  On resource: ACS first-party app ($AcsFirstPartyAppId)" -ForegroundColor DarkCyan

        if ($WhatIf) {
            Write-Host "[WhatIf] New-EntraApplication -DisplayName '$EntraAppRegistrationName'" -ForegroundColor DarkGray
            $entraAppClientId = "<WILL_BE_GENERATED>"
        }
        else {
            $entraApp = New-EntraApplication -DisplayName $EntraAppRegistrationName -RequiredResourceAccess $requiredResourceAccess
            $entraAppClientId = $entraApp.AppId
            Write-Host "Entra App created successfully." -ForegroundColor Green
            Write-Host "  Client ID: $entraAppClientId" -ForegroundColor White
            Write-Host "  ► Provide this Client ID to the Azure admin for Bot Service creation." -ForegroundColor Yellow
        }
    }
}
#endregion

#region Phase 2 — Provision Resource Account
if (ShouldRunPhase 'Phase2') {
    Write-Phase "Phase 2/3" "Provision Teams Resource Account"

    if (-not $entraAppClientId -or $entraAppClientId -eq "<WILL_BE_GENERATED>") {
        throw "Phase 2 requires the Entra App Client ID. Run Phase 1 first or provide -ExistingEntraAppClientId."
    }

    Write-Host "Connecting to Microsoft Teams ($TeamsTenantId)..." -ForegroundColor Cyan
    if (-not $WhatIf) {
        Connect-MicrosoftTeams -TenantId $TeamsTenantId
        Connect-Graph -Scopes User.ReadWrite.All, Organization.Read.All
    }

    Write-Host "Creating resource account '$TeamsResourceAccountUpn'..." -ForegroundColor Cyan
    Write-Host "  ApplicationId: $entraAppClientId" -ForegroundColor DarkCyan
    Write-Host "  ACS Resource:  $AcsCommunicationServiceGlobalId" -ForegroundColor DarkCyan

    if ($WhatIf) {
        Write-Host "[WhatIf] New-CsOnlineApplicationInstance" -ForegroundColor DarkGray
        $teamsResourceAccountObjectId = "<WILL_BE_GENERATED>"
    }
    else {
        $teamsResourceAccount = New-CsOnlineApplicationInstance `
            -UserPrincipalName $TeamsResourceAccountUpn `
            -ApplicationId $entraAppClientId `
            -DisplayName $TeamsResourceAccountDisplayName

        $teamsResourceAccountObjectId = $teamsResourceAccount.ObjectId
        Write-Host "Resource Account created. ObjectId: $teamsResourceAccountObjectId" -ForegroundColor Green

        Write-Host "Linking resource account to ACS resource..." -ForegroundColor Cyan
        Set-CsOnlineApplicationInstance `
            -Identity $teamsResourceAccount.UserPrincipalName `
            -ApplicationId $entraAppClientId `
            -AcsResourceId $AcsCommunicationServiceGlobalId

        Write-Host "Syncing resource account to Agent Provisioning Service..." -ForegroundColor Cyan
        Sync-CsOnlineApplicationInstance `
            -ObjectId $teamsResourceAccount.ObjectId `
            -ApplicationId $entraAppClientId

        Write-Host "Resource account provisioned and synced." -ForegroundColor Green
        Write-Host "  ► Provide the ObjectId ($teamsResourceAccountObjectId) to the Azure admin for TPE assignment." -ForegroundColor Yellow
    }
}
#endregion

#region Phase 3 — License and Assign Phone Number
if (ShouldRunPhase 'Phase3') {
    Write-Phase "Phase 3/3" "License and Assign Phone Number"

    $raUpn = $TeamsResourceAccountUpn

    if (-not $WhatIf) {
        # Ensure Teams connection
        try { Get-CsOnlineUser -Identity $raUpn -ErrorAction Stop | Out-Null }
        catch {
            Write-Host "Connecting to Microsoft Teams ($TeamsTenantId)..." -ForegroundColor Cyan
            Connect-MicrosoftTeams -TenantId $TeamsTenantId
            Connect-Graph -Scopes User.ReadWrite.All, Organization.Read.All
        }

        # Wait for propagation to Entra ID
        Write-Host "Waiting for resource account to appear in Entra ID..." -ForegroundColor Cyan
        $retryCount = 0
        $maxRetries = 20
        do {
            $retryCount++
            Write-Host "  Attempt $retryCount/$maxRetries — checking for $raUpn..." -ForegroundColor DarkCyan
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

        # Set usage location
        Write-Host "Setting usage location to '$TeamsUsageLocation'..." -ForegroundColor Cyan
        Update-MgUser -UserId $raUpn -UsageLocation $TeamsUsageLocation
        Start-Sleep 15

        # Assign license
        Write-Host "Assigning Teams Phone Resource Account license (SKU $TeamsPhoneRASkuId)..." -ForegroundColor Cyan
        $licenseRetry = 0
        $licenseSuccess = $false
        do {
            $licenseRetry++
            Start-Sleep 15
            try {
                Set-MgUserLicense -UserId $raUpn `
                    -AddLicenses @(@{SkuId = $TeamsPhoneRASkuId}) `
                    -RemoveLicenses @()
                $licenseSuccess = $true
            }
            catch {
                Write-Host "  Attempt $licenseRetry failed: $($_.Exception.Message)" -ForegroundColor Yellow
            }
        } until ($licenseSuccess -or ($licenseRetry -ge 10))

        if (-not $licenseSuccess) { throw "License assignment failed after $licenseRetry attempts." }
        Write-Host "License assigned." -ForegroundColor Green

        # Assign phone number
        Write-Host "Assigning phone number $TeamsPhoneNumber ($PhoneNumberType)..." -ForegroundColor Cyan
        Set-CsPhoneNumberAssignment `
            -Identity $raUpn `
            -PhoneNumber $TeamsPhoneNumber `
            -PhoneNumberType $PhoneNumberType
        Write-Host "Phone number assigned." -ForegroundColor Green

        # Capture ObjectId if we don't have it
        if (-not $teamsResourceAccountObjectId) {
            $raInstance = Get-CsOnlineApplicationInstance -Identity $raUpn
            $teamsResourceAccountObjectId = $raInstance.ObjectId
        }
    }
    else {
        Write-Host "[WhatIf] Would assign license (SKU $TeamsPhoneRASkuId) and phone number $TeamsPhoneNumber to $raUpn" -ForegroundColor DarkGray
    }
}
#endregion

#region Output
$output = [ordered]@{
    teamsTenantId                = $TeamsTenantId
    entraAppClientId             = $entraAppClientId
    teamsResourceAccountUpn      = $TeamsResourceAccountUpn
    teamsResourceAccountObjectId = $teamsResourceAccountObjectId
    phoneNumber                  = $TeamsPhoneNumber
}

$outputJson = $output | ConvertTo-Json -Depth 3
Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "  Teams Tenant Setup Complete" -ForegroundColor Green
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host ""
Write-Host $outputJson
Write-Host ""

if (-not $WhatIf) {
    $outputJson | Set-Content -Path $OutputFile -Encoding utf8
    Write-Host "Output written to: $OutputFile" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Next step: Run setup_tpe_azure.ps1 with -TeamsOutputFile '$OutputFile'" -ForegroundColor Yellow
}
#endregion
