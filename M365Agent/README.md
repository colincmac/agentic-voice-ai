# Welcome to Microsoft 365 Agents Toolkit!

## Prerequisites
### Teams development
- https://learn.microsoft.com/en-us/microsoftteams/platform/concepts/build-and-test/prepare-your-o365-tenant#enable-custom-teams-apps-and-turn-on-custom-app-uploading

### Phone System Requirements
https://learn.microsoft.com/en-us/powershell/module/microsoftteams/set-csonlineapplicationinstance?view=teams-ps#-acsresourceid
https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/tpe/teams-phone-extensibility-quickstart#teams-admin-provision-resource-account

New-CsOnlineApplicationInstance -UserPrincipalName myteamsphoneresourceaccount@contoso.com -ApplicationId aa123456-1234-1234-1234-aaa123456789 -DisplayName "My Teams Phone Resource Account"

## Quick Start

1. Press F5, or select Debug > Start Debugging menu in Visual Studio to start your app
</br>![image](https://raw.githubusercontent.com/OfficeDev/TeamsFx/dev/docs/images/visualstudio/debug/debug-button.png)
2. In Microsoft 365 Agents Playground from the launched browser, type and send "helloWorld" to your app to trigger a response


## Run the app on other platforms

The Teams app can run in other platforms like Outlook and Microsoft 365 app. See https://aka.ms/vs-ttk-debug-multi-profiles for more details.

## Get more info

New to Teams app development or Microsoft 365 Agents Toolkit? Explore Teams app manifests, cloud deployment, and much more in the https://aka.ms/teams-toolkit-vs-docs.

Learn more advanced topic like how to customize your workflow bot code in 
tutorials at https://aka.ms/teamsfx-card-action-response


## Report an issue

Select Visual Studio > Help > Send Feedback > Report a Problem. 
Or, create an issue directly in our GitHub repository:
https://github.com/OfficeDev/TeamsFx/issues
