# Configuration variables
$moduleName = "MicrosoftTeams"
$moduleVersion = "7.5.0"

# Azure Tenant/Subscription
$azureTenantId = "<YOUR_AZURE_TENANT_ID>"
$azureSubscriptionId = "<YOUR_AZURE_SUBSCRIPTION_ID>"
$azureResourceGroupName = "<YOUR_RESOURCE_GROUP_NAME>"
$azureCommunicationServiceGlobalId = "<YOUR_ACS_GLOBAL_ID>"
$azureCommunicationServicesName = "<YOUR_ACS_NAME>"
$azureBotServiceName = "<YOUR_BOT_SERVICE_NAME>"

# Teams Tenant
$teamsTenantId = "<YOUR_TENANT_ID>"

$entraAppRegistrationName = "IVR Agent Identity"
$entraAppClientId = "<YOUR_APPLICATION_CLIENT_ID>"

# https://auth.msft.communication.azure.comTeamsExtension.ManageCalls
$teamsExtensionPermission_ManageCalls = "9ed60762-c537-4e50-8984-4b1db3d922ce"

# Teams Resource Account
$teamsResourceAccountUpn = "ivr@contoso.com"
$teamsResourceAccountDescription = "IVR Agent"
$teamsPhoneNumber = "+1 610 518 8952"

$teamsResourceAccountObjectId = "<YOUR_APPLICATION_OBJECT_ID>"

# Azure Resources
az login --tenant $azureTenantId
## Create bot in Azure Bot Service
az bot create --sku S1 -n $azureBotServiceName --subscription $azureSubscriptionId --app-type "MultiTenant" --resource-group $azureResourceGroupName --name $azureBotServiceName --appid $entraAppClientId --tenant-id $teamsTenantId --subscription $azureSubscriptionId --app-type "MultiTenant" --resource-group $azureResourceGroupName --name $azureBotServiceName --appid $entraAppClientId --tenant-id $teamsTenantId
# CREATE / UPDATE (upsert) an teams extension assignment to the ACS resource
$acsTeamsExtRequestBody = @{
  principalType = "teamsResourceAccount"
  clientIds     = @()
} | ConvertTo-Json

az rest `
  --method PUT `
  --url "https://$azureCommunicationServicesName.unitedstates.communication.azure.com/access/teamsExtension/tenants/$teamsTenantId/assignments/$teamsResourceAccountObjectId`?api-version=2025-06-30" `
  --resource "https://communication.azure.com" `
  --headers "Content-Type=application/json" `
  --body $acsTeamsExtRequestBody


# Teams Resources
## Step 1 : Create Entra App in Teams Tenant
# Install-Module -Name Microsoft.Graph -RequiredVersion 2.36.1 -Repository PSGallery -Scope CurrentUser -Force -AllowClobber
# Connect-MgGraph -TenantId $teamsTenantId -Scopes Application.ReadWrite.All
# https://github.com/maciejporebski/azure-ad-first-party-apps-permissions/blob/master/apps/Azure%20Communication%20Services.md

Install-Module -Name Microsoft.Entra -RequiredVersion 1.2.0 -Repository PSGallery -Scope CurrentUser -Force -AllowClobber
Connect-Entra -Scopes "Application.ReadWrite.All", "AppRoleAssignment.ReadWrite.All" -TenantId $teamsTenantId
# $entraAppServicePrincipal = New-EntraServicePrincipal -AppId $entraApp.AppId

### Azure Communication Services Entra https://learn.microsoft.com/en-us/azure/communication-services/concepts/interop/tpe/teams-phone-extensibility-troubleshooting#consent-blocked-due-to-microsoft-entra-app-permission
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
$entraApp = New-EntraApplication -DisplayName $entraAppRegistrationName -RequiredResourceAccess $requiredResourceAccess


# Step 2 Create Teams Resource Account Associated with the Entra App
Update-Module -Name $moduleName -RequiredVersion $moduleVersion

## Connect to Microsoft Teams
Connect-MicrosoftTeams -TenantId $teamsTenantId
Connect-Graph -Scopes User.ReadWrite.All, Organization.Read.All

## Create a new Teams Phone Resource Account application instance
$teamsResourceAccount = New-CsOnlineApplicationInstance -UserPrincipalName $teamsResourceAccountUpn -ApplicationId $entraApp.AppId -DisplayName $teamsResourceAccountDescription
## Assign a phone number to the Teams Phone Resource Account
## Teams Microsoft Teams Phone Resource Account SKU ID is 440eaaa8-b3e0-484b-a8be-62870b9ba70a -- see here: https://learn.microsoft.com/en-us/entra/identity/users/licensing-service-plan-reference
$teamsRATeamsPhoneSkuId = "440eaaa8-b3e0-484b-a8be-62870b9ba70a"
$addLicensesRequest = @(
    @{SkuId = $teamsRATeamsPhoneSkuId}
)
Set-MgUser -UserId $teamsResourceAccount.UserPrincipalName -UsageLocation US
Set-MgUserLicense -UserId $teamsResourceAccount.UserPrincipalName -AddLicenses $addLicensesRequest -RemoveLicenses @()
Set-CsPhoneNumberAssignment -Identity $teamsResourceAccountUpn -PhoneNumber $teamsPhoneNumber -PhoneNumberType CallingPlan

## Configure application instance with ACS resource
### Documentation: https://learn.microsoft.com/en-us/powershell/module/microsoftteams/set-csonlineapplicationinstance?view=teams-ps
### https://learn.microsoft.com/en-us/powershell/module/microsoft.graph.users.actions/set-mguserlicense?view=graph-powershell-1.0
Set-CsOnlineApplicationInstance -Identity $teamsResourceAccount.UserPrincipalName -ApplicationId $entraAppClientId -AcsResourceId $azureCommunicationServiceGlobalId
Sync-CsOnlineApplicationInstance -ObjectId $teamsResourceAccount.Id


Set-CsOnlineApplicationInstance -Identity ivr-agent@M365MCP80022685.onmicrosoft.com -ApplicationId "10ec1b27-38db-4fc0-a3de-cc4ec20a9661"  -AcsResourceId $azureCommunicationServiceGlobalId
Sync-CsOnlineApplicationInstance -ObjectId 748a7c7c-a489-4de4-8a68-4415f1d19a9c -ApplicationId 10ec1b27-38db-4fc0-a3de-cc4ec20a9661
Set-CsPhoneNumberAssignment -Identity ivr-agent@M365MCP80022685.onmicrosoft.com -PhoneNumber "+16105188952"




