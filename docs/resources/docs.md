<!--
================================================================================
  VENDOR DOCUMENTATION SNAPSHOT — DO NOT EDIT
  Source : https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/tpe/
           (teams-phone-extensibility-quickstart + teams-phone-extensibility-answer-teams-calls)
  Captured: 2026-04-27
  Purpose : Cached reference for offline grounding. For authoritative content,
            always consult the canonicalUrl in each section's front-matter.
  Edits   : Do not hand-edit. To refresh, re-fetch from the canonical URLs.
            Project-specific deltas live in docs/tpe-onboarding-guide.md.
================================================================================
-->
---
layout: Conceptual
title: Teams Phone Extensibility - An Azure Communication Services article | Microsoft Learn
canonicalUrl: https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/tpe/teams-phone-extensibility-quickstart
breadcrumb_path: /azure/bread/toc.json
feedback_help_link_url: https://learn.microsoft.com/answers/tags/128/azure-communication-services/
feedback_help_link_type: get-help-at-qna
feedback_product_url: https://feedback.azure.com/d365community/forum/81ff6d2b-0c25-ec11-b6e6-000d3a4f0858
feedback_system: Standard
permissioned-type: public
recommendations: true
recommendation_types:
- Training
- Certification
uhfHeaderId: azure
ms.suite: office
adobe-target: true
learn_banner_products:
- azure
description: This article describes how to extend Teams Phone System with Azure Communication Services.
author: radubulboaca
ms.service: azure-communication-services
ms.subservice: call-automation
ms.topic: tutorial
ms.date: 2025-09-02T00:00:00.0000000Z
ms.author: radubulboaca
ms.custom: general_availability
services: azure-communication-services
locale: en-us
document_id: f648ae7f-fc91-f459-71c7-63dd53f9b1c3
document_version_independent_id: 0ecf214a-ddce-d7bb-3f44-76dfee254402
updated_at: 2026-02-03T23:16:00.0000000Z
original_content_git_url: https://github.com/MicrosoftDocs/azure-docs-pr/blob/live/articles/communication-services/quickstarts/tpe/teams-phone-extensibility-quickstart.md
gitcommit: https://github.com/MicrosoftDocs/azure-docs-pr/blob/f8f2c0cdf279203a4656e4689493951a96f30837/articles/communication-services/quickstarts/tpe/teams-phone-extensibility-quickstart.md
git_commit_id: f8f2c0cdf279203a4656e4689493951a96f30837
site_name: Docs
depot_name: Azure.azure-documents
page_type: conceptual
toc_rel: ../../toc.json
pdf_url_template: https://learn.microsoft.com/pdfstore/en-us/Azure.azure-documents/{branchName}{pdfName}
word_count: 3106
asset_id: communication-services/quickstarts/tpe/teams-phone-extensibility-quickstart
moniker_range_name:
monikers: []
item_type: Content
source_path: articles/communication-services/quickstarts/tpe/teams-phone-extensibility-quickstart.md
cmProducts:
- https://authoring-docs-microsoft.poolparty.biz/devrel/63959238-cb90-4871-a33d-4a5519097e47
- https://authoring-docs-microsoft.poolparty.biz/devrel/54fcef21-b24a-4ef4-9c4a-a525c23ee9a3
spProducts:
- https://authoring-docs-microsoft.poolparty.biz/devrel/78d87f42-5582-4a6b-90be-7db2f12b34e6
- https://authoring-docs-microsoft.poolparty.biz/devrel/8ab60220-e56b-41ff-a8bf-45b8d7ab716f
platformId: a9ac436b-82a7-e501-03c2-bdc64d6d181f
---

# Teams Phone Extensibility - An Azure Communication Services article | Microsoft Learn

This article describes how an independent software vendor (ISV) can provision Teams Phone for the scope of Teams Phone extensibility (TPE). This article also describes how an ISV can guide their customers because there are operations in Teams that a tenant needs to implement.

## Presumptions

- ISV’s Customer has access to Teams Admin Center.
- ISV’s Customer has access to Microsoft 365 Admin Center.
- ISV has access to change Azure Communication Services Resource settings.
- You grant Teams Tenant access to a CCaaS service for Graph API usage.
- ISV uses the .NET, JavaScript, or Java ACS Call Automation SDK version 1.5.0 or above (Python version will be released soon).
- ISV uses the JavaScript ACS Client SDK version 1.37 and above.

## Quick start

The rest of this article describes quick starts for two different personas: CCaaS Developer and Teams Tenant. The CCaaS developer is the ISV persona building the CCaaS service using Azure Communication Services. The Teams Tenant is the persona that's a customer of the ISV that's administering to Teams Phone.

### CCaaS Developer: Provision the AppID (Application ID)

Before you can create a bot, you need to register an Application ID.

1. From the Azure portal, open **App Registrations**.

    [![Screen capture showing the Azure portal with App registration selected from services.](media/teams-phone-extensibility-app-registration.png)](media/teams-phone-extensibility-app-registration.png#lightbox)
2. Select **New registration**.
3. Complete the required fields and click **Register**.
4. When the portal completes the resource, click **Go to resource**.
5. Record the values for the **Application (client) ID** and **Directory (tenant) ID**.

    [![Screen capture showing the Azure portal App registrations resource displaying Essentials including Application (client) ID and Directory (tenant) ID.](media/teams-phone-extensibility-app-registration-resources.png)](media/teams-phone-extensibility-app-registration-resources.png#lightbox)
6. Open **Certificates & secrets**. Create new a client secret and record the certificate and secret ID values.

For more information, see [Registering a calling bot](https://microsoftgraph.github.io/microsoft-graph-comms-samples/docs/articles/calls/register-calling-bot.html#registering-an-app-registration).

### CCaaS Developer: Create the Bot

Once you create the `AppID`, Teams Phone system also uses the current process as defined for Graph to create a bot.

Alternatively, you can complete the following steps to create just the bot after you create an Application ID using the Azure CLI.

1. Download Azure CLI.
2. Sign in with your Azure account used for the preceding App Registration:

    ```azurecli
    az login
    ```
3. If not already installed, install `Az.BotService`:

    ```azurecli
    Install-Module Az.BotService -AllowClobber
    ```
4. Provision the bot using the following command. For more information, see [Publish a bot with Azure PowerShell - Azure AI Bot Service - Bot Service](/en-us/azure/bot-service/powershell/bot-builder-powershell-quickstart):

If your environment is already provisioned, skip the following steps.

```azurecli
Connect-AzAccount

Install-Module Az.Resources

Register-AzResourceProvider -ProviderNamespace Microsoft.BotService
```

Required:

```azurecli
New-AzBotService -ResourceGroupName <your RG name here> -Name "<name of Teams Phone bot>" -ApplicationId <your Application/ClientID from Entra> -Location <bot location> -Sku S1 -Description "<description of bot>"
```

Example:

```azurecli
New-AzBotService -ResourceGroupName teamsphonetest-rg -Name "teamsPhoneBot" -ApplicationId aa123456-1234-1234-1234-aaa123456789 -Location "global" -Sku S1 -Description "My Teams Phone Test Bot"
```

For this step, when you configure Teams, the webhook can be any URL. Enter any valid URL such as `https://mycompanydomain.com`.

Note

In the future, we expect to remove this dependency on URL.

### Teams Admin: Provision Resource Account

The Teams Administrator needs to run the following cmdlets. The Teams Admin needs to provision a Teams Resource Account for Teams Phone extensibility via cmdlets. Teams Admin Center is out of scope, requiring PowerShell 6.1.1 or greater.

Sign in to Teams PowerShell and update to the latest version by running this command:

Example:

```azurecli
Connect-MicrosoftTeams

Update-Module MicrosoftTeams
```

Use the [New-CsOnlineApplicationInstance (MicrosoftTeamsPowerShell)](/en-us/powershell/module/teams/new-csonlineapplicationinstance) cmdlet to create a Resource Account. There's no change for Teams Phone extensibility in this command. The ApplicationId parameter is your third party bot ID. Don't use the Teams first person Application identifiers (IDs) defined in [Set-CsOnlineApplicationInstance (MicrosoftTeamsPowerShell)](/en-us/powershell/module/teams/set-csonlineapplicationinstance#-applicationid) because they don't work for Teams Phone extensibility. It's up to the CCaaS developer on how to communicate the Application ID to the Teams Tenant.

Example:

```azurecli
 New-CsOnlineApplicationInstance -UserPrincipalName myteamsphoneresourceaccount@contoso.com -ApplicationId aa123456-1234-1234-1234-aaa123456789 -DisplayName "My Teams Phone Resource Account"
```

Use the updated [Set-CSonlineApplicationInstance](/en-us/powershell/module/skype/set-csonlineapplicationinstance) command to assign the Resource Account to your Azure Communication Services Resource. This command routes calls to that Azure Communication Services Resource. It's up to the CCaaS developer on how to communicate the `ACSResourceID` to the Teams Tenant.

Example:

```azurecli
Set-CsOnlineApplicationInstance -Identity myteamsphoneresourceaccount@contoso.com -ApplicationId aa123456-1234-1234-1234-aaa123456789 -AcsResourceId bb567890-1234-1234-1234-bbb123456789
```

Use the updated [Sync-CsOnlineApplicationInstance (MicrosoftTeamsPowerShell)](/en-us/powershell/module/teams/sync-csonlineapplicationinstance) command to sync the Resource Account to your Agent Provisioning Service. This command routes calls to that Azure Communication Services Resource. There's no change for Teams Phone extensibility in this command.

Example:

```azurecli
Sync-CsOnlineApplicationInstance -ObjectId cc123456-5678-5678-1234-ccc123456789 -ApplicationId aa123456-1234-1234-1234-aaa123456789
```

Optionally, you can use the [Get-CsOnlineApplicationInstance (MicrosoftTeamsPowerShell)](/en-us/powershell/module/teams/get-csonlineapplicationinstance) cmdlet to return the provisioned Resource Account.

### Teams Admin: Acquire and assign a phone number to Resource Account

You need to assign a public switched telephone network (PSTN) number to your Resource Account (RA) to make a call to this endpoint. Let’s provision a Teams Phone Number and assign it to your recently provisioned Teams Resource Account.

1. Go to your [Teams Admin Center](https://admin.teams.microsoft.com/dashboard).
2. Sign in with your Teams admin user credentials.
3. Go to the **Phone Number** section and provision your choice of Teams Phone number **Service** type. For more information, see [Get service phone numbers for Calling Plans - Microsoft Teams](/en-us/microsoftteams/getting-service-phone-numbers). Once provisioned, you need to assign the phone number to your Resource Account.
4. Run the following commands to assign the Teams Phone number to your Resource Account:

    ```azurecli
    Set-CsPhoneNumberAssignment -Identity <your-TeamsResourceAccount> -PhoneNumber <acquired-number> -PhoneNumberType <DirectRouting|CallingPlan|OperatorConnect>
    ```
5. If you aren't sure about your phone number's details, run the following `get` cmdlet to get the details of the phone number:

    ```azurecli
    Get-CsPhoneNumberAssignment -TelephoneNumber <acquired-number>
    ```
6. Did the Teams Portal alert you about the lack of appropriate license? If so, you need to assign proper license to your Resource Account.

    1. Go to your [Microsoft 365 Admin Center](https://admin.microsoft.com).
    2. Go to **Licenses**, and assign **Microsoft Teams Phone Resource Account** to your Resource Account.
7. Also, if you plan to make outbound PSTN calls using your Resource Accounts assigned phone number, now is a good time to assign a [Microsoft Teams Calling Plan](/en-us/microsoftteams/calling-plans-for-office-365).

>
> **Note:** Proper configuration depends on the phone number service type assigned to the Resource account:
>
> - **Direct Routing** – The tenant must be configured for Direct Routing with a verified Session Border Controller (SBC), a Direct Routing phone number assigned to the Resource account, and a Voice Routing Policy that allows PSTN calls assigned to the Resource account.
> - **Calling Plan** – The Resource account must be assigned a Calling Plan service number and licensed with Microsoft Teams Phone Resource Account.
> - **Operator Connect** – The phone number must be provisioned by an approved Operator Connect provider that supports outbound PSTN calling for voice applications.
> - For all these connectivity options, you need to make sure you have the proper Microsoft licenses in place as detailed in the [general license prerequisite](/en-us/azure/communication-services/concepts/interop/tpe/teams-phone-extensibility-overview#prerequisites) or specifically for [Outbound license requirements](/en-us/azure/communication-services/quickstarts/tpe/teams-phone-extensibility-server-outbound-call#licensing-requirements)
>

### CCaaS Developer: Get Resource Account Information

We're introducing a new Graph API to get a list of Resource Accounts and phone numbers where assigned. The Graph API supports an optional filter on your Microsoft Entra first party `applicationID` / `clientId`.

Authentication:

The Graph API supports *Delegated* authentication as currently defined in [Graph Authentication](/en-us/graph/auth/auth-concepts).

Authorization:

The Microsoft Entra ID application needs to request [Microsoft Graph permissions reference TeamsResourceAccount.Read.All](/en-us/graph/permissions-reference#teamsresourceaccountreadall). To assign that permission, follow the steps in [How to Assign Delegated Graph Permissions](/en-us/entra/identity-platform/quickstart-configure-app-access-web-apis#delegated-permission-to-microsoft-graph).

The Microsoft Entra application also needs [Administrator consent](/en-us/entra/identity-platform/permissions-consent-overview#administrator-consent).

Once you grant the Microsoft Entra application appropriate Graph permissions, then you need to assign the user to that application. Follow the steps in [Manage users and groups assignment to an application](/en-us/entra/identity/enterprise-apps/assign-user-or-group-access-portal).

The CCaaS Admin also needs elevated permissions to access Teams Resource Account information. The Graph API is getting Teams Resource Account information and that information is an asset owned by Teams Admin, so it requires privileged access as a Teams Admin. For more information, see [Permissions for Managing Resource Accounts](/en-us/microsoftteams/manage-resource-accounts#assign-permissions-for-managing-a-resource-account).

### Querying Teams Resource Accounts with Filtering and Paging

The Teams Resource Accounts API supports **OData filtering**, **server‑side paging**, and **cursor-based continuation** via the `@odata.nextLink` field. This capability enables CCaaS developers to efficiently retrieve Resource Accounts associated with their Azure Communication Services (ACS) resources.

#### Query definition

>
> `https://graph.microsoft.com/beta/admin/teams/resourceAccounts`

You can extend the base query with OData options such as:

- `$filter`—filter Resource Accounts by fields such as `appId` or `acsResourceId`
- `$top`—limit the number of results returned per page
- `@odata.nextLink`—Graph-provided URL for retrieving subsequent pages

### Filter by ACS Resource ID

The following example returns only Resource Accounts that are associated with a specific ACS Resource ID:

```rest
GET https://graph.microsoft.com/beta/admin/teams/resourceAccounts?$filter=acsResourceId eq 'aa123456-1234-1234-1234-aaa123456789'
```

Successful response:

```rest
{
  "@odata.context": "https://graph.microsoft.com/beta/$metadata#admin/teams/resourceAccounts",
  "value": [
    {
      "id": "aa123456-1234-1234-1234-aaa123456789",
      "userPrincipalName": "myteamsphoneresourceaccount@contoso.com",
      "displayName": "My RA Name",
      "appId": "aa123456-1234-1234-1234-aaa123456789",
      "phoneNumber": "tel:+1234567890",
      "acsResourceId": "aa123456-1234-1234-1234-aaa123456789"
    }
  ]
}
```

### Paging with $top

You can use $top to specify the maximum number of results per page:

```rest
GET https://graph.microsoft.com/beta/admin/teams/resourceAccounts?$top=5
```

If more than five Resource Accounts exist, Microsoft Graph returns an @odata.nextLink value for the next page:

```rest

{
  "value": [
    {
      "id": "...",
      "displayName": "...",
      "acsResourceId": "..."
    }
  ],
  "@odata.nextLink":
    "https://graph.microsoft.com/beta/admin/teams/resourceAccounts?$top=5&$skiptoken=abc123..."
}
```

To continue paging, call the URL in the @odata.nextLink field until it's no longer present.

### Combined paging and filtering

Developers can combine $top and $filter to retrieve a filtered, paginated list:

```rest
GET https://graph.microsoft.com/beta/admin/teams/resourceAccounts?$top=5&$filter=acsResourceId eq 'aa123456-1234-1234-1234-aaa123456789'
```

This query returns up to five Resource Accounts that match the specified ACS Resource ID.

### C# Example—Filtering + Paging

The following C# sample demonstrates how to:

1. Query Teams Resource Accounts
2. Filter by acsResourceId
3. Limit results to 5 per page
4. Follow @odata.nextLink for full pagination

```csharp
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

public static async Task<List<JObject>> GetResourceAccountsAsync(
    string acsResourceId,
    string accessToken)
{
    var results = new List<JObject>();

    string requestUrl =
        $"https://graph.microsoft.com/beta/admin/teams/resourceAccounts" +
        $"?$top=5&$filter=acsResourceId eq '{acsResourceId}'";

    string nextUrl = requestUrl;

    while (!string.IsNullOrEmpty(nextUrl))
    {
        var request = new HttpRequestMessage(HttpMethod.Get, nextUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using (var client = new HttpClient())
        {
            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var page = JObject.Parse(json);

            if (page["value"] is JArray items)
            {
                foreach (var item in items)
                {
                    results.Add((JObject)item);
                }
            }

            // Continue to the next page if returned
            nextUrl = (string)page["@odata.nextLink"];
        }
    }

    return results;
}
```

### CCaaS Developer: Receive and answer incoming call

The following steps demonstrate how to receive and answer an incoming Teams call.

#### Prerequisites

1. An Azure account with an active subscription. [Create an account for free](https://azure.microsoft.com/pricing/purchase-options/azure-account?cid=msft_learn).
2. A deployed Communication Services resource. [Create a Communication Services resource](/en-us/azure/communication-services/quickstarts/create-communication-resource).
3. A configured Event Grid endpoint: [Incoming call concepts - An Azure Communication Services concept document | Microsoft Learn](../../concepts/call-automation/incoming-call-notification#receiving-an-incoming-call-notification-from-event-grid).
4. A Teams Phone number assigned to the resource account.
5. A Teams Resource Account provisioned to send calls to the Azure Communication Services Resource.
6. A Teams Resource Account provisioned with a Calling Plan.
7. An Azure Communication Services Resource with permission to receive call from the Teams Resource Account.
8. [Create and host a dev tunnel](/en-us/azure/developer/dev-tunnels/get-started).
9. (Optional) Create a Microsoft Teams user with a phone license that is voice enabled. Teams Phone license is required to add Teams users to the call. Learn more about [Microsoft Teams business options](https://www.microsoft.com/microsoft-teams/compare-microsoft-teams-business-options). For more information, see [Set up Teams Phone in your organization](/en-us/microsoftteams/setting-up-your-phone-system).
10. Complete client and server consent as defined in [Access a user's Teams Phone separate from their Teams client](https://github.com/Azure/communication-preview/blob/master/Teams%20Phone%20Extensibility/teams-phone-extensibility-access-teams-phone.md).

Note

For the Azure Communication Services resource, ensure the data location matches the Teams Tenant location to comply with data boundary regulations. You can retrieve programmatically details about tenant organization via [Get organization](/en-us/graph/api/organization-get)

#### Setup and host your Azure dev tunnels

Azure dev tunnels enable you to share local web services hosted on the internet. Run the commands to connect your local development environment to the public internet. Dev tunnels create a persistent endpoint URL that enables anonymous access. We use this endpoint to notify your application about calling events from the Azure Communication Services Call Automation service.

```dotnetcli
devtunnel create --allow-anonymous
devtunnel port create -p 8080
devtunnel host
```

Alternatively, follow instructions to set up your Azure dev tunnels in [Visual Studio](/en-us/aspnet/core/test/dev-tunnels).

#### Handle call automation callback events

```csharp
app.MapPost("/api/callbacks", async (CloudEvent[] cloudEvents, ILogger < Program > logger) => {
  foreach(var cloudEvent in cloudEvents) {
    logger.LogInformation($"Event received: {JsonConvert.SerializeObject(cloudEvent)}");
    CallAutomationEventBase parsedEvent = CallAutomationEventParser.Parse(cloudEvent);
    logger.LogInformation($"{parsedEvent?.GetType().Name} parsedEvent received for call connection id: {parsedEvent?.CallConnectionId}");
    var callConnection = callAutomationClient.GetCallConnection(parsedEvent.CallConnectionId);
    var callMedia = callConnection.GetCallMedia();
    if (parsedEvent is CallConnected) {
      //Handle Call Connected Event
    }
  }
});
```

#### Handle an Incoming Call event and answer the call

```csharp
app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        // Handle system events
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            // Handle the subscription validation event.
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }
        }

        var jsonObject = Helper.GetJsonObject(eventGridEvent.Data);
        var fromPhoneNumber = new PhoneNumberIdentifier(Helper.GetFrom(jsonObject));
        var toPhoneNumber = new PhoneNumberIdentifier(Helper.GetTo(jsonObject));
        var incomingCallContext = Helper.GetIncomingCallContext(jsonObject);

        var callbackUri = new Uri(new Uri(devTunnelUri), $"/api/callbacks");
        var options = new AnswerCallOptions(incomingCallContext, callbackUri);

        AnswerCallResult answerCallResult = await callAutomationClient.AnswerCallAsync(options);
        logger.LogInformation($"Answered call for connection id: {answerCallResult.CallConnection.CallConnectionId}");

        //Use EventProcessor to process CallConnected event
        var answer_result =  await answerCallResult.WaitForEventProcessorAsync();

        if (answer_result.IsSuccess)
        {
           logger.LogInformation($"Call connected event received for connection id: {answer_result.SuccessResult.CallConnectionId}");
           var callConnectionMedia = answerCallResult.CallConnection.GetCallMedia();
        }
    }
    return Results.Ok();
});
```

The Incoming Call notification is formatted as follows. There's no change to the schema. However the `to:rawid` now reflects the identity of the Teams Resource Account GUID:

Sample Incoming Call Event with Teams Resource Account Identifier and custom context (VoIP and SIP):

```json
{
  "to": {
    "kind": "unknown",
    "rawId": "28:orgid:cc123456-5678-5678-1234-ccc123456789"
  },
  "from": {
    "kind": "phoneNumber",
    "rawId": "4:+12065551212",
    "phoneNumber": {
      "value": "+12065551212"
    }
  },
  "serverCallId": "aHR0cHM6Ly9hcGkuZmxpZ2h0cHJveHkudGVhbXMubWljcm9zb2Z0LmNvbS9hcGkvdjIvZXAvY29udi11c3dlLTAyLXNkZi1ha3MuY29udi5za3lwZS5jb20vY29udi9fVERMUjZVS3BrT05aTlRMOHlIVnBnP2k9MTAtNjAtMTMtMjE2JmU9NjM4NTMwMzUzMjk2MjI3NjY1",
  "callerDisplayName": "+12065551212",
  "customContext":
   {
       "voipHeaders":
        {
           "X-myCustomVoipHeaderName": "myValue"
        },
  "incomingCallContext": "<CALL_CONTEXT VALUE>",
  "correlationId": "2e0fa6fe-bf3e-4351-9beb-568add4f5315"
}
```

### CCaaS Developer: How to access media directly

If the built-in PlayTo, recognize and recording options that come with the Call Automation SDK don't meet the needs of the CCaaS service, the CCaaS developer can access media directly. You can access media for calls can be accessed via REST + Websocket notification mechanisms when instantiating the `AnswerCallOptions` object. Once the call is established, media flow via WebSocket notifications to the URL provided in the `MediaStreamingOptions` object. See the following example code snippet:

The following options aren't specific to Teams Phone extensibility and are also available to non Teams Phone extensibility flows.

```csharp
var mediaStreamingOptions = new MediaStreamingOptions(
    new Uri("wss://mywebsocket.azure.com/client/hubs/media?accesstoken={access_token}"),
      MediaStreamingTransport.WebSocket,
      MediaStreamingContent.Audio,
      MediaStreamingAudioChannel.Mixed,
    );
    var answerCallOptions = new AnswerCallOptions(incomingCallContext, callbackUri: new Uri(callConfiguration.AppCallbackUrl)) {
      MediaStreamingOptions = mediaStreamingOptions
    };
    var response = await callingServerClient.AnswerCallAsync(answerCallOptions);
```

The following code is a schema definition of a streaming data object. The audio packets are base64 encoded and CCaaS needs to decode the value in the `data` attribute to get the pulse-code modulation (PCM) bytes.

```json
{
    "kind": <string>, // What kind of data this is, e.g. AudioMetadata, AudioData.
    "audioData":{
        "data": <string>, // Base64 Encoded audio buffer data
        "timestamp": <string>, // In ISO 8601 format (yyyy-mm-ddThh:mm:ssZ)
        "participantRawID": <string>,
        "silent": <boolean> // Indicates if the received audio buffer contains only silence.
    }
}
```

### CCaaS Client Developer: How to authenticate as dual persona

Developer needs to acquire the Teams Tenant ID and client ID. The developer needs to implement this acquisition. Once the client application acquires the Tenant ID and client ID, the developer implements the means to prompt the user. For more information, see [InteractiveBrowserCredential Class (Azure.Identity)](/en-us/dotnet/api/azure.identity.interactivebrowsercredential).

Once the user interaction is complete and auth is successful, a token is returned and passed to [AzureCommunicationTokenCredential class](/en-us/javascript/api/@azure/communication-common/azurecommunicationtokencredential) to return an Azure Communication Services token.

Parse the Azure Communication Services token to get the [CommunicationUserIdentifier interface | Microsoft Learn](/en-us/javascript/api/@azure/communication-common/communicationuseridentifier).

See the code sample defined in [Create a credential capable of obtaining a Microsoft Entra user token](https://github.com/Azure/communication-preview/blob/master/Teams%20Phone%20Extensibility/teams-phone-extensibility-access-teams-phone.md#create-a-credential-capable-of-obtaining-a-microsoft-entra-user-token).

### CCaaS Client Developer: How to construct the Teams Phone extensibility Call Agent

Developer instantiates a calling client, then calls the [createTeamsCallAgent](/en-us/javascript/api/azure-communication-services/@azure/communication-calling/callclient#@azure-communication-calling-callclient-createteamscallagent) method. For more information, see [CallClient class](/en-us/javascript/api/azure-communication-services/@azure/communication-calling/callclient).

```javascript
...
//Auth and get token
...
this._teamsCallAgent = await this._callClient.createTeamsCallAgent(this.tokenCredential);
```

### CCaaS Client Developer: How to place an outbound OBO call

Developers need to get the on-behalf-of (OBO) Resource Account identity that the call needs to be placed on behalf of. The following articles describe how to place an outbound OBO call.

Once the OBO identity is acquired, you need to set the `onBehalfOfOptions` in the `StartTeamsGroupCallOptions()` or `StartTeamsCallOptions()` method. For more information, see [StartTeamsGroupCallOptions interface](/en-us/javascript/api/azure-communication-services/@azure/communication-calling/startteamsgroupcalloptions) or [StartTeamsCallOptions interface](/en-us/javascript/api/azure-communication-services/@azure/communication-calling/startteamscalloptions).

Once you set the call options, then use the `startCall()` method in the [TeamsCallAgent interface](/en-us/javascript/api/azure-communication-services/@azure/communication-calling/teamscallagent).

```javascript
...
...
 const isMultipleParticipants = Array.isArray(userIds) && userIds.length > 1;
 this._previousTeamsCall = this._currentTeamsCall;
 var onBehalfOfOptions: SDK.OnBehalfOfOptions | undefined;
 if (this.elements.onBehalfOfUserInput.value !== null && this.elements.onBehalfOfUserInput.value !== "" ) {
     var onBehalfOfUser = myFunctionToGetIdofResourceAccount();
     if (isMicrosoftTeamsAppIdentifier(onBehalfOfUser)) {
         onBehalfOfOptions = onBehalfOfUser ? { userId: onBehalfOfUser } : undefined;
         if (onBehalfOfOptions) {
             console.log("OBO options provided with app Id: " + (onBehalfOfUser as MicrosoftTeamsAppIdentifier).teamsAppId);
         }
     } else {
         console.error("OBO option ignored, MicrosoftTeamsAppIdentifier type expected");
     }
  }
 if (isMultipleParticipants) {
      const participants = userIds as (MicrosoftTeamsUserIdentifier | PhoneNumberIdentifier | MicrosoftTeamsAppIdentifier | UnknownIdentifier)[];
     (this._placeCallOptions as SDK.StartTeamsGroupCallOptions).onBehalfOfOptions = onBehalfOfOptions;
     call = this._teamsCallAgent.startCall(participants, this._placeCallOptions as SDK.StartTeamsGroupCallOptions);
 } else {
     const participant = userIds[0] as (MicrosoftTeamsUserIdentifier | PhoneNumberIdentifier | MicrosoftTeamsAppIdentifier | UnknownIdentifier);
     (this._placeCallOptions as SDK.StartTeamsCallOptions).onBehalfOfOptions = onBehalfOfOptions;
     call = this._teamsCallAgent.startCall(participant, this._placeCallOptions as SDK.StartTeamsCallOptions);
 }
...
...
```

### CCaaS developer: How to address a Teams Phone extensibility call agent by a server

After you sign in as an agent with a dual persona identity, you can add that Teams Phone extensibility call agent to an established call using their dual persona identity.

The following example shows a request to add a Teams Phone extensibility call agent with Microsoft Entra ID identifier `0269be4d-5be0-4770-bf9c-a1bf50ee78d5` in tenant `87d349ed-44d7-43e1-9a83-5f2406dee5bd` scoped to Azure Communication Services Resource `e5b7f628-ea94-4fdc-b3d9-1af1fe231111`.

```csharp
//Call is already established
...
...
 var target = new TeamsExtensionUserIdentifier("0269be4d-5be0-4770-bf9c-a1bf50ee78d5", "87d349ed-44d7-43e1-9a83-5f2406dee5bd","e5b7f628-ea94-4fdc-b3d9-1af1fe231111");
 await callConnection.AddParticipantAsync(new AddParticipantOptions(new CallInvite(target))
 {
     InvitationTimeoutInSeconds = 60,
     OperationContext = "addParticipantAsync"
 });
...
...
```

### CCaaS developer: How to add a PSTN user to a Teams Phone extensibility call by a server

Once you establish a Teams Phone extensibility call, you can then add a PSTN user to the call using a phone number.

The following example shows a request to add a PSTN user to a Teams Phone extensibility call with phone number `+12065551212`.

```csharp
//Call is already established
...
...
var target = new PhoneNumberIdentifier("+12065551212");
await callConnection.AddParticipantAsync(new AddParticipantOptions(new CallInvite(target, null))
{
    InvitationTimeoutInSeconds = 60,
    OperationContext = "addParticipantAsync"
});
...
...
```

### CCaaS developer: How to transfer a Teams Phone extensibility call by a server to a PSTN endpoint

Once you establish a Teams Phone extensibility call, you can then transfer it to a PSTN user by specifying a phone number.

The following example shows a request to transfer an established call to a PSTN user with phone number `+12065551212`.

```csharp
//Call is already established
...
...
var target = new PhoneNumberIdentifier("+12065551212");
await callConnection.TransferCallToParticipantAsync(new TransferToParticipantOptions(target)
{
    OperationContext = "transferParticipantAsync"
});
...
...
```

### CCaaS developer: How to start recording session with StartRecordingOptions

For Teams Phone extensibility, you need to use the CallConnectionId received during initiation of the call, when starting the recording session.

- Use RecordingContent to pass the recording content type. Use AUDIO.
- Use RecordingChannel to pass the recording channel type. Use MIXED or UNMIXED.
- Use RecordingFormat to pass the format of the recording. Use WAV.

```csharp
CallAutomationClient callAutomationClient = new CallAutomationClient("<ACSConnectionString>");

StartRecordingOptions recordingOptions = new StartRecordingOptions("<callConnectionId>")
{
    RecordingContent = RecordingContent.Audio,
    RecordingChannel = RecordingChannel.Unmixed,
    RecordingFormat = RecordingFormat.Wav,
    RecordingStateCallbackUri = new Uri("<CallbackUri>");
};
Response<RecordingStateResult> response = await callAutomationClient.GetCallRecording()
.StartAsync(recordingOptions);
```

Note

Recording started with connection ID is started async (204 response code) and recording state change is updated via callback event `Microsoft.Communication.RecordingStateChanged` received on `RecordingStateCallbackUri`.

In addition, any failure to start recording is reported via a new callback event `Microsoft.Communication.StartRecordingFailed` received on `RecordingStateCallbackUri`.
----
---
layout: Conceptual
title: Answer Teams Phone calls from Call Automation - An Azure Communication Services article | Microsoft Learn
canonicalUrl: https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/tpe/teams-phone-extensibility-answer-teams-calls
breadcrumb_path: /azure/bread/toc.json
feedback_help_link_url: https://learn.microsoft.com/answers/tags/128/azure-communication-services/
feedback_help_link_type: get-help-at-qna
feedback_product_url: https://feedback.azure.com/d365community/forum/81ff6d2b-0c25-ec11-b6e6-000d3a4f0858
feedback_system: Standard
permissioned-type: public
recommendations: true
recommendation_types:
- Training
- Certification
uhfHeaderId: azure
ms.suite: office
adobe-target: true
learn_banner_products:
- azure
description: This article describes how to receive and answer incoming Teams Phone Extensibility calls on Azure Communication Services.
author: sofiar
manager: miguelher
services: azure-communication-services
ms.author: sofiar
ms.date: 2025-09-01T00:00:00.0000000Z
ms.topic: quickstart
ms.service: azure-communication-services
ms.subservice: identity
locale: en-us
document_id: 665cb0bb-6102-157f-8834-16602dd3f841
document_version_independent_id: f7d4d0e5-2a14-8996-ff58-3fb9cfcf0b99
updated_at: 2025-10-08T05:12:00.0000000Z
original_content_git_url: https://github.com/MicrosoftDocs/azure-docs-pr/blob/live/articles/communication-services/quickstarts/tpe/teams-phone-extensibility-answer-teams-calls.md
gitcommit: https://github.com/MicrosoftDocs/azure-docs-pr/blob/0c42627eaa81400dfbf6531e773cbbcfca29893d/articles/communication-services/quickstarts/tpe/teams-phone-extensibility-answer-teams-calls.md
git_commit_id: 0c42627eaa81400dfbf6531e773cbbcfca29893d
site_name: Docs
depot_name: Azure.azure-documents
page_type: conceptual
toc_rel: ../../toc.json
pdf_url_template: https://learn.microsoft.com/pdfstore/en-us/Azure.azure-documents/{branchName}{pdfName}
word_count: 544
asset_id: communication-services/quickstarts/tpe/teams-phone-extensibility-answer-teams-calls
moniker_range_name:
monikers: []
item_type: Content
source_path: articles/communication-services/quickstarts/tpe/teams-phone-extensibility-answer-teams-calls.md
cmProducts:
- https://authoring-docs-microsoft.poolparty.biz/devrel/63959238-cb90-4871-a33d-4a5519097e47
- https://authoring-docs-microsoft.poolparty.biz/devrel/54fcef21-b24a-4ef4-9c4a-a525c23ee9a3
spProducts:
- https://authoring-docs-microsoft.poolparty.biz/devrel/78d87f42-5582-4a6b-90be-7db2f12b34e6
- https://authoring-docs-microsoft.poolparty.biz/devrel/8ab60220-e56b-41ff-a8bf-45b8d7ab716f
platformId: deca5d28-3831-c707-0930-6aa9ea849826
---

# Answer Teams Phone calls from Call Automation - An Azure Communication Services article | Microsoft Learn

Use Azure Communication Services Call Automation to receive and answer calls for a Teams resource account.

## Prerequisites

- An Azure account with an active subscription. [Create an account for free](https://azure.microsoft.com/pricing/purchase-options/azure-account?cid=msft_learn).
- A Communication Services resource, see [Create a Communication Services resource](../create-communication-resource).
- A configured Event Grid endpoint. [Incoming call concepts - An Azure Communication Services concept document](../../concepts/call-automation/incoming-call-notification#receiving-an-incoming-call-notification-from-event-grid)
- A Microsoft Teams resource account with an associated phone number. [Create a new Teams Resource account](/en-us/powershell/module/teams/new-csonlineapplicationinstance)
- Create and host an Azure Dev Tunnel. [Instructions here](/en-us/azure/developer/dev-tunnels/get-started).

## Associate your Teams resource account with your Communication Services resource

Execute the following command. Make sure you have your Azure Communication Services resource identifier ready. To find it, see [Get an immutable resource identifier](/en-us/azure/communication-services/concepts/troubleshooting-info#get-an-immutable-resource-id).

```powershell
Set-CsOnlineApplicationInstance -Identity <appIdentity> -AcsResourceId <acsResourceId>
```

For more details, follow this guide: [Associate your Azure Communication Services resource with the Teams Resource account](/en-us/powershell/module/teams/set-csonlineapplicationinstance#-acsresourceid)

## Configure your Communication Services resource to accept calls for the Teams resource account

Send a request to the Microsoft Teams Extension access assignments API to allow receiving calls for the Teams resource account. For more details on how to authenticate the web request, follow this guide: [Authentication](/en-us/rest/api/communication/authentication)

The following example shows a request for a Teams Tenant with identifier `87d349ed-44d7-43e1-9a83-5f2406dee5bd` and a Teams resource account oid with identifier `e5b7f628-ea94-4fdc-b3d9-1af1fe231111`.

```http
PUT {endpoint}/access/teamsExtension/tenants/87d349ed-44d7-43e1-9a83-5f2406dee5bd/assignments/e5b7f628-ea94-4fdc-b3d9-1af1fe231111?api-version=2025-06-30

{
    "principalType" : "teamsResourceAccount",
}
```

The `{principalType}` needs to be `teamsResourceAccount`.

### Response

The following example shows the response.

```http
HTTP/1.1 201 Created
Content-type: application/json

{
    "objectId": "e5b7f628-ea94-4fdc-b3d9-1af1fe231111",
    "tenantId": "87d349ed-44d7-43e1-9a83-5f2406dee5bd",
    "principalType" : "teamsResourceAccount",
}
```

## Stop accepting calls for the Teams resource account

Send a request to the Microsoft Teams Extension access assignments API to delete the entry for your Teams resource account.

```http
DELETE {endpoint}/access/teamsExtension/assignments/e5b7f628-ea94-4fdc-b3d9-1af1fe231111?api-version=2025-06-30
```

### Response

```http
HTTP/1.1 204 NoContent
Content-type: application/json

{}
```

To verify that the Teams resource account is no longer linked with the Communication Services resource, you can send a GET request to the Microsoft Teams Extension access assignments API. Verify that its response status code is 404.

```http
GET {endpoint}/access/teamsExtension/assignments/e5b7f628-ea94-4fdc-b3d9-1af1fe231111?api-version=2025-06-30
```

## Receive and answer incoming calls

### Setup and host your Azure DevTunnel

DevTunnels create a persistent endpoint URL which allows anonymous access. We use this endpoint to notify your application of calling events from the Azure Communication Services Call Automation service.

```powershell
devtunnel create --allow-anonymous

devtunnel port create -p 8080

devtunnel host
```

### Handle Incoming Call event and answer the call

```csharp
app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    if (eventGridEvent.TryGetSystemEventData(out object systemEvent))
    {
        switch (systemEvent)
        {
            case SubscriptionValidationEventData subscriptionValidated:
               var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);

            case AcsIncomingCallEventData incomingCall:
                var callbackUri = new Uri(new Uri(devTunnelUri), $"/api/callbacks");
                var options = new AnswerCallOptions(incomingCallContext, callbackUri);

                AnswerCallResult answerCallResult = await callAutomationClient.AnswerCallAsync(options);
                logger.LogInformation($"Answered call for connection id: {answerCallResult.CallConnection.CallConnectionId}");

                //Use EventProcessor to process CallConnected event

                var answerResult =  await answerCallResult.WaitForEventProcessorAsync();
                if (answerResult.IsSuccess)
                {
                   logger.LogInformation($"Call connected event received for connection id: {answerResult.SuccessResult.CallConnectionId}");
                   var callConnectionMedia = answerCallResult.CallConnection.GetCallMedia();
                }
                return Results.Ok();

            default:
                logger.LogInformation($"Received unexpected event of type {eventGridEvent.EventType}");
                return Results.BadRequest();
        }
    }
    return Results.Ok();
});
```

### Sample Incoming Call event with Teams resource account identifier and custom context (VoIP and SIP)

```json
{
  "to": {
    "kind": "unknown",
    "rawId": "28:orgid:cc123456-5678-5678-1234-ccc123456789"
  },
  "from": {
    "kind": "phoneNumber",
    "rawId": "4:+12065551212",
    "phoneNumber": {
      "value": "+12065551212"
    }
  },
  "serverCallId": "aHR0cHM6Ly9hcGkuZmxpZ2h0cHJveHkudGVhbXMubWljcm9zb2Z0LmNvbS9hcGkvdjIvZXAvY29udi11c3dlLTAyLXNkZi1ha3MuY29udi5za3lwZS5jb20vY29udi9fVERMUjZVS3BrT05aTlRMOHlIVnBnP2k9MTAtNjAtMTMtMjE2JmU9NjM4NTMwMzUzMjk2MjI3NjY1",
  "callerDisplayName": "+12065551212",
  "customContext":
  {
    "voipHeaders":
    {
        "X-myCustomVoipHeaderName": "myValue"
    },
  },
  "incomingCallContext": "<CALL_CONTEXT VALUE>",
  "correlationId": "2e0fa6fe-bf3e-4351-9beb-568add4f5315"
}
```
