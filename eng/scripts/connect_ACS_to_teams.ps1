# Configuration variables
$moduleName = "MicrosoftTeams"
$moduleVersion = "7.5.0"
$tenantId = "<YOUR_TENANT_ID>"
$applicationInstanceIdentity = "ivr@contoso.com"
$applicationId = "<YOUR_APPLICATION_OBJECT_ID>"
$acsResourceId = "<YOUR_ACS_GLOBAL_ID>"

# Update Teams module
Update-Module -Name $moduleName -RequiredVersion $moduleVersion

# Connect to Microsoft Teams
Connect-MicrosoftTeams -TenantId $tenantId

# Configure application instance with ACS resource
Set-CsOnlineApplicationInstance -Identity $applicationInstanceIdentity -ApplicationId $applicationId -AcsResourceId $acsResourceId
