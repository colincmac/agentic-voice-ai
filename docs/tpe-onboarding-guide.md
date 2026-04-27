# Teams Phone Extensibility (TPE) — Enterprise Onboarding Guide

This guide walks through the end-to-end process of connecting Azure Communication Services (ACS) with Teams Phone via Teams Phone Extensibility (TPE). It supplements the [official quickstart](https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/tpe/teams-phone-extensibility-quickstart) and [answer-calls guide](https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/tpe/teams-phone-extensibility-answer-teams-calls) with enterprise-scale automation scripts and operational details not covered in the public docs.

## Scripts

Provisioning is split into two scripts separated by **tenant boundary** and **permission scope**:

| Script | Tenant | Required Roles | Phases |
|--------|--------|----------------|--------|
| [`setup_tpe_teams.ps1`](../eng/scripts/setup_tpe_teams.ps1) | Teams (M365) | Global Admin **or** Application Admin + SfB Admin + User Admin | 1. Entra App → 2. Resource Account → 3. License + Phone |
| [`setup_tpe_azure.ps1`](../eng/scripts/setup_tpe_azure.ps1) | Azure | Contributor on resource group | 1. Bot Service → 2. ACS TPE Authorization |

The Teams script runs **first** and outputs a JSON file (`tpe-teams-output.json`) containing the Entra App Client ID and Resource Account Object ID. The Azure script consumes that file.

For environments where the **same admin** manages both tenants, use the orchestrator [`setup_tpe.ps1`](../eng/scripts/setup_tpe.ps1) which calls both in sequence.

### Dependency Graph

```
setup_tpe_teams.ps1 (Teams Admin)              setup_tpe_azure.ps1 (Azure Admin)
─────────────────────────────────               ─────────────────────────────────
Phase 1: Entra App Registration ──────┬──────► Phase 1: Azure Bot Service
    │                                 │              (needs Entra App Client ID)
    ▼                                 │
Phase 2: Teams Resource Account ──────┤
    │                                 │
    ▼                                 └──────► Phase 2: ACS TPE Authorization
Phase 3: License + Phone Number                      (needs RA Object ID)
```

### Quick Start

```powershell
# Option A: Separate admins (recommended for enterprise)
# --- Teams Admin runs: ---
.\setup_tpe_teams.ps1 -ConfigFile .\tpe-config.sample.json

# --- Azure Admin runs (after receiving tpe-teams-output.json): ---
.\setup_tpe_azure.ps1 -ConfigFile .\tpe-config.sample.json -TeamsOutputFile .\tpe-teams-output.json

# Option B: Single admin with access to both tenants
.\setup_tpe.ps1 -ConfigFile .\tpe-config.sample.json
```

## Two-Tenant Architecture

TPE requires resources across **two separate tenants**:

| Tenant | Purpose | Required Roles |
|--------|---------|----------------|
| **Azure Tenant** | Hosts Azure subscription with ACS resource, Bot Service, and application infrastructure | Contributor on the resource group |
| **Teams Tenant** | Hosts Microsoft 365 / Teams Phone with resource accounts, phone numbers, and licensing | Global Admin **or** Skype for Business Administrator + User Administrator |

> **Important:** The ACS resource data location must match the Teams tenant location to comply with data boundary regulations. Verify via [Get organization](https://learn.microsoft.com/en-us/graph/api/organization-get).

```
┌─────────────────────────────────┐       ┌─────────────────────────────────────┐
│         Azure Tenant            │       │          Teams Tenant               │
│                                 │       │                                     │
│  ┌───────────────────────────┐  │       │  ┌─────────────────────────────┐    │
│  │ Azure Communication       │  │       │  │ Entra App Registration      │    │
│  │ Services (ACS)            │◄─┼───────┼──│  - TeamsExtension.          │    │
│  │  - TPE assignment API     │  │       │  │    ManageCalls permission   │    │
│  └───────────────────────────┘  │       │  └─────────────────────────────┘    │
│                                 │       │                 │                   │
│  ┌───────────────────────────┐  │       │  ┌─────────────────────────────┐    │
│  │ Azure Bot Service         │  │       │  │ Teams Resource Account      │    │
│  │  - AppId = Entra App      │  │       │  │  - Linked to Entra App      │    │
│  │    ClientId               │  │       │  │  - Phone number assigned    │    │
│  │  - TenantId = Teams       │  │       │  │  - Licensed (RA + Calling)  │    │
│  │    Tenant ID              │  │       │  │  - AcsResourceId set        │    │
│  └───────────────────────────┘  │       │  └─────────────────────────────┘    │
│                                 │       │                                     │
└─────────────────────────────────┘       └─────────────────────────────────────┘
```

## Prerequisites

### Modules

| Module | Version | Purpose |
|--------|---------|---------|
| `MicrosoftTeams` | ≥ 7.5.0 | Teams resource account provisioning |
| `Microsoft.Entra` | ≥ 1.2.0 | Entra app registration in Teams tenant |
| `Microsoft.Graph.Users.Actions` | latest | License assignment via `Set-MgUserLicense` |
| Azure CLI (`az`) | latest | Bot Service creation + ACS TPE assignment API |

### Well-Known Constants

| Constant | Value | Description |
|----------|-------|-------------|
| ACS First-Party App ID | `1fd5118e-2576-4263-8130-9503064c837a` | `https://auth.msft.communication.azure.com` |
| TeamsExtension.ManageCalls Permission ID | `9ed60762-c537-4e50-8984-4b1db3d922ce` | Required API permission for TPE |
| Teams Phone Resource Account SKU ID | `440eaaa8-b3e0-484b-a8be-62870b9ba70a` | License SKU for resource accounts |
| TPE Assignment API Version | `2025-06-30` | Current API version for ACS TPE endpoints |

---

## Step-by-Step Provisioning

> **Execution order:** The Teams script runs first (all 3 phases). Once Phase 1 completes, the Azure admin can start in parallel. Azure Phase 2 requires the RA Object ID from Teams Phase 2.

### Teams Script — Phase 1: Create Entra App Registration

**Script:** `setup_tpe_teams.ps1` Phase 1 | **Persona:** Teams Tenant Global Admin or Application Administrator

This is the **root dependency** — both the Azure Bot Service and the Teams Resource Account need the Entra App Client ID. Create an app registration **in the Teams tenant** with the `TeamsExtension.ManageCalls` permission for ACS.

```powershell
# Install the Entra module
Install-Module -Name Microsoft.Entra -RequiredVersion 1.2.0 -Repository PSGallery -Scope CurrentUser -Force -AllowClobber

# Connect to the Teams tenant
Connect-Entra -Scopes "Application.ReadWrite.All", "AppRoleAssignment.ReadWrite.All" -TenantId $teamsTenantId

# Define the required ACS permission
$requiredResourceAccess = @(
    @{
        resourceAppId  = "1fd5118e-2576-4263-8130-9503064c837a"  # ACS first-party app
        resourceAccess = @(
            @{
                id   = "9ed60762-c537-4e50-8984-4b1db3d922ce"    # TeamsExtension.ManageCalls
                type = "Scope"
            }
        )
    }
)

# Create the Entra app registration
$entraApp = New-EntraApplication -DisplayName $entraAppRegistrationName -RequiredResourceAccess $requiredResourceAccess

# Record the Application (Client) ID — this is used as the Bot ApplicationId and RA ApplicationId
Write-Host "Entra App Client ID: $($entraApp.AppId)"
```

> **Troubleshooting:** If consent is blocked, see [consent troubleshooting](https://learn.microsoft.com/en-us/azure/communication-services/concepts/interop/tpe/teams-phone-extensibility-troubleshooting#consent-blocked-due-to-microsoft-entra-app-permission).

After this phase, **share the Entra App Client ID with the Azure admin** so they can create the Bot Service in parallel with the remaining Teams phases.

---

### Teams Script — Phase 2: Provision Resource Account

**Script:** `setup_tpe_teams.ps1` Phase 2 | **Persona:** Teams Admin (Skype for Business Administrator + User Administrator)

This creates the Teams Resource Account, links it to the Entra app, and associates it with the ACS resource.

```powershell
# Connect to Microsoft Teams and Microsoft Graph
Connect-MicrosoftTeams -TenantId $teamsTenantId
Connect-Graph -Scopes User.ReadWrite.All, Organization.Read.All

# 3a. Create the resource account
#     ApplicationId = Entra App Client ID (NOT a first-party Teams app ID)
$teamsResourceAccount = New-CsOnlineApplicationInstance `
    -UserPrincipalName $teamsResourceAccountUpn `
    -ApplicationId $entraApp.AppId `
    -DisplayName $teamsResourceAccountDescription

# 3b. Link the resource account to ACS
Set-CsOnlineApplicationInstance `
    -Identity $teamsResourceAccount.UserPrincipalName `
    -ApplicationId $entraApp.AppId `
    -AcsResourceId $azureCommunicationServiceGlobalId

# 3c. Sync to the Agent Provisioning Service
Sync-CsOnlineApplicationInstance `
    -ObjectId $teamsResourceAccount.ObjectId `
    -ApplicationId $entraApp.AppId
```

> **Critical:** Do **not** use Teams first-party Application IDs (Auto Attendant, Call Queue, etc.) as the `ApplicationId`. You must use your own Entra app's Client ID.

---

### Teams Script — Phase 3: License and Assign Phone Number

**Script:** `setup_tpe_teams.ps1` Phase 3 | **Persona:** Teams Admin

The resource account must be licensed and have a phone number before it can receive calls. There is a propagation delay (15–60 seconds) after creating the resource account before it appears in Entra ID.

```powershell
# Wait for the resource account to propagate to Entra ID
do {
    Write-Host "Waiting for resource account to appear in Entra ID..." -ForegroundColor Cyan
    Start-Sleep 15
    $resourceAccountObject = Get-MgUser -UserId $teamsResourceAccount.UserPrincipalName
} until ($resourceAccountObject.UserPrincipalName -eq $teamsResourceAccount.UserPrincipalName)

# 4a. Set usage location (required for license assignment)
Update-MgUser -UserId $teamsResourceAccount.UserPrincipalName -UsageLocation "US"
Start-Sleep 15

# 4b. Assign the Teams Phone Resource Account license
$teamsRATeamsPhoneSkuId = "440eaaa8-b3e0-484b-a8be-62870b9ba70a"
do {
    $error.Clear()
    Start-Sleep 15
    Set-MgUserLicense -UserId $teamsResourceAccount.UserPrincipalName `
        -AddLicenses @(@{SkuId = $teamsRATeamsPhoneSkuId}) `
        -RemoveLicenses @()
} until (!$error)

# 4c. Assign the phone number
Set-CsPhoneNumberAssignment `
    -Identity $teamsResourceAccount.UserPrincipalName `
    -PhoneNumber $teamsPhoneNumber `
    -PhoneNumberType CallingPlan    # or DirectRouting / OperatorConnect
```

**Phone number type considerations:**

| Type | Requirements |
|------|-------------|
| **Calling Plan** | Resource account must have a Calling Plan service number and the RA license |
| **Direct Routing** | Tenant must have a verified SBC, DR phone number, and Voice Routing Policy assigned to the RA |
| **Operator Connect** | Number must be provisioned by an approved OC provider that supports outbound PSTN for voice apps |

> **Outbound calls:** If the resource account needs to make outbound PSTN calls, also assign a [Microsoft Teams Calling Plan](https://learn.microsoft.com/en-us/microsoftteams/calling-plans-for-office-365) license.

---

After this phase completes, share the `tpe-teams-output.json` file (or the RA Object ID) with the Azure admin for Phase 2 of the Azure script.

---

### Azure Script — Phase 1: Create Bot Service

**Script:** `setup_tpe_azure.ps1` Phase 1 | **Persona:** Azure Subscription Contributor

The Bot Service links your Entra App (from the Teams tenant) to Azure. The `ApplicationId` is the Client ID of the Entra App Registration, and the `TenantId` is the **Teams tenant** ID. This phase can run **in parallel** with Teams Phases 2 and 3 once the Entra App Client ID is available.

```powershell
# Sign in to the Azure tenant
az login --tenant $azureTenantId

# Register the Bot Service resource provider (first-time only)
Register-AzResourceProvider -ProviderNamespace Microsoft.BotService

# Create the bot — ApplicationId is your Entra App Client ID, TenantId is the TEAMS tenant
az bot create `
    --resource-group $azureResourceGroupName `
    --name $azureBotServiceName `
    --app-type "MultiTenant" `
    --appid $entraAppClientId `
    --tenant-id $teamsTenantId `
    --sku S1 `
    --location "global" `
    --subscription $azureSubscriptionId
```

> **Note:** The webhook URL can be any valid URL (e.g. `https://yourcompany.com`). Microsoft plans to remove this dependency in future.

---

### Azure Script — Phase 2: Authorize ACS to Accept Calls (TPE Assignment)

**Script:** `setup_tpe_azure.ps1` Phase 2 | **Persona:** Azure Subscription Contributor (must be logged in to Azure CLI)

This is the step the public docs describe as "Configure your Communication Services resource to accept calls for the Teams resource account." It calls the ACS TPE Assignment API to create an authorization linking the Teams resource account to the ACS resource.

Use the provided [`azure_acs_tpe_auth.ps1`](../eng/scripts/azure_acs_tpe_auth.ps1) script:

```powershell
# Ensure you're logged into the Azure tenant
az login --tenant $azureTenantId

# Authorize ACS to accept calls for the Teams resource account
.\eng\scripts\azure_acs_tpe_auth.ps1 `
    -AzureCommunicationServicesName $azureCommunicationServicesName `
    -TeamsTenantId $teamsTenantId `
    -TeamsResourceAccountObjectId $teamsResourceAccount.ObjectId `
    -PrincipalType "teamsResourceAccount" `
    -Verbose
```

**What the script does:**
1. Constructs the ACS TPE assignment API URL:
   ```
   PUT https://{acsName}.communication.azure.com/access/teamsExtension/tenants/{teamsTenantId}/assignments/{raObjectId}?api-version=2025-06-30
   ```
2. Sends a JSON body with `principalType: "teamsResourceAccount"` and an optional `clientIds` array
3. Uses `az rest` with `--resource https://communication.azure.com` for Entra-based authentication
4. Writes the JSON body to a temp file to avoid Windows shell escaping issues with `az.cmd`

**Verify the assignment:**
```powershell
az rest `
    --method GET `
    --url "https://$azureCommunicationServicesName.communication.azure.com/access/teamsExtension/tenants/$teamsTenantId/assignments/$($teamsResourceAccount.ObjectId)?api-version=2025-06-30" `
    --resource "https://communication.azure.com"
```

**Remove the assignment (stop accepting calls):**
```powershell
az rest `
    --method DELETE `
    --url "https://$azureCommunicationServicesName.communication.azure.com/access/teamsExtension/assignments/$($teamsResourceAccount.ObjectId)?api-version=2025-06-30" `
    --resource "https://communication.azure.com"
```

---

## Verification Checklist

After completing all phases, verify the setup:

| # | Check | Command |
|---|-------|---------|
| 1 | Resource account exists and is linked to ACS | `Get-CsOnlineApplicationInstance -Identity $teamsResourceAccountUpn` |
| 2 | Phone number is assigned | `Get-CsPhoneNumberAssignment -TelephoneNumber $teamsPhoneNumber` |
| 3 | ACS TPE assignment exists | `az rest --method GET --url "https://{acsName}.communication.azure.com/access/teamsExtension/tenants/{teamsTenantId}/assignments/{raObjectId}?api-version=2025-06-30" --resource "https://communication.azure.com"` |
| 4 | Bot Service is configured | `az bot show --name $azureBotServiceName --resource-group $azureResourceGroupName` |

---

## Provisioning at Scale

For enterprise customers managing many resource accounts, the Entra App and Bot Service are created **once**. The per-account steps (Teams Phases 2–3 + Azure Phase 2) are looped per resource account. This is naturally split across the two admin roles:

**Teams Admin** — batch provision resource accounts:
```powershell
$resourceAccounts = Import-Csv "resource-accounts.csv"
# Expected columns: UPN, DisplayName, PhoneNumber, PhoneNumberType

Connect-MicrosoftTeams -TenantId $teamsTenantId
Connect-Graph -Scopes User.ReadWrite.All, Organization.Read.All

foreach ($ra in $resourceAccounts) {
    # Teams Phase 2: Create & link resource account
    $account = New-CsOnlineApplicationInstance `
        -UserPrincipalName $ra.UPN `
        -ApplicationId $entraAppClientId `
        -DisplayName $ra.DisplayName

    Set-CsOnlineApplicationInstance -Identity $ra.UPN `
        -ApplicationId $entraAppClientId `
        -AcsResourceId $azureCommunicationServiceGlobalId

    Sync-CsOnlineApplicationInstance -ObjectId $account.ObjectId -ApplicationId $entraAppClientId

    # Teams Phase 3: License & phone number (with retry for propagation)
    # ... (wait for propagation, set usage location, assign license, assign phone)
}
# Export ObjectIds for the Azure admin
$resourceAccounts | Select-Object UPN, ObjectId | Export-Csv "ra-objectids.csv"
```

**Azure Admin** — batch authorize ACS for each resource account:
```powershell
$accounts = Import-Csv "ra-objectids.csv"

foreach ($ra in $accounts) {
    .\eng\scripts\azure_acs_tpe_auth.ps1 `
        -AzureCommunicationServicesName $azureCommunicationServicesName `
        -TeamsTenantId $teamsTenantId `
        -TeamsResourceAccountObjectId $ra.ObjectId `
        -PrincipalType "teamsResourceAccount"
}
```

---

## Key Identity Relationships

Understanding which ID goes where is the most common source of confusion:

| Field | Value | Where It's Used |
|-------|-------|-----------------|
| **Entra App Client ID** | `AppId` from `New-EntraApplication` | Bot Service `--appid`, RA `ApplicationId`, `Set-CsOnlineApplicationInstance -ApplicationId` |
| **ACS Resource Global ID** | Immutable resource ID from Azure portal | `Set-CsOnlineApplicationInstance -AcsResourceId`, TPE assignment API URL |
| **Teams Tenant ID** | Directory ID of the M365/Teams tenant | Bot Service `--tenant-id`, `Connect-MicrosoftTeams -TenantId`, TPE assignment API URL |
| **RA Object ID** | `ObjectId` from `New-CsOnlineApplicationInstance` | `Sync-CsOnlineApplicationInstance -ObjectId`, TPE assignment API URL path |
| **ACS Resource Name** | DNS-friendly name of the ACS resource | TPE assignment API hostname: `{name}.communication.azure.com` |

> **Finding the ACS Resource Global ID:** In the Azure portal, navigate to your ACS resource → Properties → Resource ID (immutable). Or use [Get an immutable resource identifier](https://learn.microsoft.com/en-us/azure/communication-services/concepts/troubleshooting-info#get-an-immutable-resource-id).

---

## Gaps in Public Documentation

The following items are **not fully documented** in the official Microsoft Learn articles and are addressed by this guide and the scripts in `eng/scripts/`:

1. **ACS TPE Assignment API body format** — The public docs show a minimal `{ "principalType": "teamsResourceAccount" }` body. The actual Entra-auth variant requires wrapping in a `{ "request": { ... } }` envelope with a `clientIds` array (can be empty). See `azure_acs_tpe_auth.ps1`.

2. **Windows shell escaping** — `az rest --body` on Windows strips embedded double-quotes from inline JSON. The `azure_acs_tpe_auth.ps1` script works around this by writing to a temp file and using `--body @filepath` syntax.

3. **Propagation delays** — After `New-CsOnlineApplicationInstance`, the resource account takes 15–60 seconds to appear in Entra ID. License assignment may also require retries. The public docs don't mention these delays.

4. **License assignment automation** — The public docs reference the M365 Admin Center UI. For automation, use `Set-MgUserLicense` with the RA SKU ID `440eaaa8-b3e0-484b-a8be-62870b9ba70a`, preceded by `Update-MgUser -UsageLocation`.

5. **ACS endpoint region suffix** — The TPE assignment API endpoint varies by region (e.g., `unitedstates.communication.azure.com` vs `communication.azure.com`). The `azure_acs_tpe_auth.ps1` script uses the base `communication.azure.com` which routes correctly.

6. **Sync is mandatory** — `Sync-CsOnlineApplicationInstance` must be called after linking the RA to ACS. Without this, call routing won't work even if the assignment API succeeds.

---

## References

- [Teams Phone Extensibility Overview](https://learn.microsoft.com/en-us/azure/communication-services/concepts/interop/tpe/teams-phone-extensibility-overview)
- [TPE Quickstart](https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/tpe/teams-phone-extensibility-quickstart)
- [Answer Teams Phone Calls from Call Automation](https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/tpe/teams-phone-extensibility-answer-teams-calls)
- [TPE Troubleshooting](https://learn.microsoft.com/en-us/azure/communication-services/concepts/interop/tpe/teams-phone-extensibility-troubleshooting)
- [Set-CsOnlineApplicationInstance](https://learn.microsoft.com/en-us/powershell/module/teams/set-csonlineapplicationinstance)
- [Licensing Service Plan Reference](https://learn.microsoft.com/en-us/entra/identity/users/licensing-service-plan-reference)
- [ACS First-Party App Permissions](https://github.com/maciejporebski/azure-ad-first-party-apps-permissions/blob/master/apps/Azure%20Communication%20Services.md)
