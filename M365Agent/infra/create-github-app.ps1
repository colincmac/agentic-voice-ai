param(
    [Parameter(Mandatory = $true)]
    [string] $Owner,
    [Parameter(Mandatory = $true)]
    [string] $Repo,
    [Parameter(Mandatory = $true)]
    [string] $AppName,
    [Parameter(Mandatory = $true)]
    [string] $BotEndpoint  # e.g. https://f3z8srb6-3978.usw2.devtunnels.ms/api/webhook"
)

$webhookUrl = "$BotEndpoint/api/webhook"
# 1. Create or update the GitHub App
# Check if the app exists
$existingApp = gh api "orgs/$Owner/apps" --jq ".[] | select(.name==`"$AppName`")" 2>$null

if ($existingApp) {
    Write-Host "GitHub App '$AppName' already exists. Please update it manually in the GitHub UI if needed."
} else {
    Write-Host "Creating GitHub App '$AppName'..."
    # This must be done via the web UI, but we can open the page for convenience
    Start-Process "https://github.com/organizations/$Owner/settings/apps/new"
    Write-Host "Fill out the form, set the callback URL as required, and save the app."
    Read-Host "Press Enter after creating the GitHub App and copying the Client ID & Secret"
}

# 2. Prompt for Client ID and Secret
$clientId = Read-Host "Enter the GitHub App Client ID"
$clientSecret = Read-Host "Enter the GitHub App Client Secret"

# 3. Set up webhook on the repository for Pull Requests
Write-Host "Creating/Updating webhook for PR events..."
$hookExists = gh api repos/$Owner/$repo/hooks --jq ".[] | select(.config.url==`"$webhookUrl`")" 2>$null

if ($hookExists) {
    Write-Host "Webhook already exists."
} else {
    gh api repos/$Owner/$Repo/hooks -X POST -F name="web" -F config.url="$webhookUrl" -F config.content_type="json" -F events[]="pull_request"
    Write-Host "Webhook created for PR events."
}

# 4. Export environment variables
$env:GITHUB_OWNER = $Owner
$env:GITHUB_REPOSITORY = $repo
$env:GITHUB_CLIENT_ID = $clientId
$env:GITHUB_CLIENT_SECRET = $clientSecret

Write-Host "Environment variables set:"
Write-Host "GITHUB_OWNER=$Owner"
Write-Host "GITHUB_REPOSITORY=$repo"
Write-Host "GITHUB_CLIENT_ID=$clientId"
Write-Host "GITHUB_CLIENT_SECRET=$clientSecret"
