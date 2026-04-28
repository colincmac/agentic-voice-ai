############### TEMP
# Azure Tenant/Subscription
$azureTenantId = "16b3c013-d300-468d-ac64-7eda0820b6d3"
$azureSubscriptionId = "9d9a4ca4-c3f1-463a-9fea-a6dcc8f4f63c"
$azureResourceGroupName = "wdg-platform-connectivity"
$azureCommunicationServiceGlobalId = "6195fece-e9d5-44a2-abd8-7621663c2a30"
$azureCommunicationServicesName = "woodgrove-ai"
$azureBotServiceName = "wdg-ivr-agent"

# Teams Tenant
$teamsTenantId = "47391752-1e97-4cf2-be12-8f84eace2873"

$entraAppRegistrationName = "IVR Agent Identity"
$entraAppClientId = "10ec1b27-38db-4fc0-a3de-cc4ec20a9661"

# Teams Resource Account
$teamsResourceAccountUpn = "ivr@M365MCP80022685.onmicrosoft.com"
$teamsResourceAccountDescription = "IVR Agent"
$teamsPhoneNumber = "+1 610 518 8952"

$teamsUsageLocation = "US"
$teamsResourceAccountObjectId = "<YOUR_APPLICATION_OBJECT_ID>"
$teamsExtensionResourceAppId = "1fd5118e-2576-4263-8130-9503064c837a" # https://auth.msft.communication.azure.com
$teamsExtensionPermissionId = "9ed60762-c537-4e50-8984-4b1db3d922ce" # TeamsExtension.ManageCalls
$requiredResourceAccess = @(
    @{
        resourceAppId = $teamsExtensionResourceAppId
        resourceAccess = @(
            @{
                id   = $teamsExtensionPermissionId
                type = "Scope"  # or "Role" depending on the permission
            }
        )
    }
)

# Teams Resources
## Step 1 : Create Entra App in Teams Tenant
# Install-Module -Name Microsoft.Graph -RequiredVersion 2.36.1 -Repository PSGallery -Scope CurrentUser -Force -AllowClobber
# Connect-MgGraph -TenantId $teamsTenantId -Scopes Application.ReadWrite.All
# https://github.com/maciejporebski/azure-ad-first-party-apps-permissions/blob/master/apps/Azure%20Communication%20Services.md

Install-Module -Name Microsoft.Entra -RequiredVersion 1.2.0 -Repository PSGallery -Scope CurrentUser -Force -AllowClobber
Connect-Entra -Scopes "Application.ReadWrite.All", "AppRoleAssignment.ReadWrite.All" -TenantId $teamsTenantId
# $entraAppServicePrincipal = New-EntraServicePrincipal -AppId $entraApp.AppId

### Azure Communication Services Entra https://learn.microsoft.com/en-us/azure/communication-services/concepts/interop/tpe/teams-phone-extensibility-troubleshooting#consent-blocked-due-to-microsoft-entra-app-permission

$entraApp = New-EntraApplication -DisplayName $entraAppRegistrationName -RequiredResourceAccess $requiredResourceAccess


# Step 2 Create Teams Resource Account Associated with the Entra App
Update-Module -Name $moduleName -RequiredVersion $moduleVersion

## Connect to Microsoft Teams
Connect-MicrosoftTeams -TenantId $teamsTenantId
Connect-Graph -Scopes User.ReadWrite.All, Organization.Read.All

## Create a new Teams Phone Resource Account application instance
$teamsResourceAccount = New-CsOnlineApplicationInstance -UserPrincipalName $teamsResourceAccountUpn -ApplicationId $entraApp.AppId -DisplayName $teamsResourceAccountDescription

# {
#   "AcsResourceId": null,
#   "ApplicationId": "10ec1b27-38db-4fc0-a3de-cc4ec20a9661",
#   "DisplayName": "IVR Agent",
#   "ObjectId": "97302657-ed0b-496f-911f-25fa953bbbd6",
#   "PhoneNumber": null,
#   "TenantId": null,
#   "UserPrincipalName": "ivr@M365MCP80022685.onmicrosoft.com"
# }
Set-CsOnlineApplicationInstance -Identity $teamsResourceAccount.UserPrincipalName -ApplicationId $entraApp.AppId -AcsResourceId $azureCommunicationServiceGlobalId
$teamsResourceAccount = Get-CsOnlineApplicationInstance -Identity $teamsResourceAccountUpn


Sync-CsOnlineApplicationInstance -ObjectId $teamsResourceAccount.ObjectId -ApplicationId $teamsResourceAccount.ApplicationId


## From here assigned the license to the resource account and phone number via the
# Wait for resource account to appear in Entra ID
Start-Sleep 15
do {

    Write-Host "Checking if the user object is already available in Entra... next try in 15s..." -ForegroundColor Cyan
    Start-Sleep 15
    $resourceAccountObject = Get-MgUser -UserId $teamsResourceAccount.UserPrincipalName

} until(
    $resourceAccountObject.UserPrincipalName -eq $teamsResourceAccount.UserPrincipalName
)

Write-Host "Setting usage location for resource account '$( $teamsResourceAccount.UserPrincipalName)'..." -ForegroundColor Cyan
Update-MgUser -UserId $teamsResourceAccount.UserPrincipalName -UsageLocation $teamsUsageLocation
Start-Sleep 15
Write-Host "Assigning license for resource account '$upn'..." -ForegroundColor Cyan
do {
    $error.Clear()
    Start-Sleep 15
    Set-MgUserLicense -UserId $upn -AddLicenses @(@{SkuId = $MCOVU}) -RemoveLicenses @()
} until (
    !$error
)
Set-CsPhoneNumberAssignment -Identity $teamsResourceAccount.UserPrincipalName -PhoneNumber $teamsPhoneNumber -PhoneNumberType CallingPlan


## Assign a phone number to the Teams Phone Resource Account
## Teams Microsoft Teams Phone Resource Account SKU ID is 440eaaa8-b3e0-484b-a8be-62870b9ba70a -- see here: https://learn.microsoft.com/en-us/entra/identity/users/licensing-service-plan-reference
$teamsRATeamsPhoneSkuId = "440eaaa8-b3e0-484b-a8be-62870b9ba70a"
$addLicensesRequest = @(
    @{SkuId = $teamsRATeamsPhoneSkuId}
)
Set-MgUserLicense -UserId $teamsResourceAccount.UserPrincipalName -AddLicenses $addLicensesRequest -RemoveLicenses @()
Set-CsPhoneNumberAssignment -Identity $teamsResourceAccountUpn -PhoneNumber $teamsPhoneNumber -PhoneNumberType CallingPlan

## Configure application instance with ACS resource
### Documentation: https://learn.microsoft.com/en-us/powershell/module/microsoftteams/set-csonlineapplicationinstance?view=teams-ps
### https://learn.microsoft.com/en-us/powershell/module/microsoft.graph.users.actions/set-mguserlicense?view=graph-powershell-1.0
Set-CsOnlineApplicationInstance -Identity $teamsResourceAccount.UserPrincipalName -ApplicationId $entraAppClientId -AcsResourceId $azureCommunicationServiceGlobalId
Sync-CsOnlineApplicationInstance -ObjectId $teamsResourceAccount.Id

## At this point we have a fully provisioned Teams Phone Resource Account linked to an Entra App Registration and Azure Communication Services resource
# - Entra App Registration
# - Teams Phone Resource Account provisioned with the appropriate license and phone number assigned
# - Application instance configured to link the Teams Resource Account with the Azure Communication Services resource

