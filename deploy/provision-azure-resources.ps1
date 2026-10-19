<#
.SYNOPSIS
    ONE-TIME Azure resource provisioning for WhatDo.StatusService.

.DESCRIPTION
    Creates (idempotently) the resource group, Linux App Service plan (Free F1 tier),
    and Web App that WhatDo.StatusService will be deployed into, enables HTTPS-only,
    and sets the shared status-ingest secret as an app setting.

    Run this yourself, once, before the first deploy.ps1 run. It is not invoked
    automatically by deploy.ps1 or any CI workflow. Safe to re-run: every `az`
    command here is idempotent (create-or-update), including the one-time
    'Microsoft.Web' resource provider registration this script performs - required
    the first time an App Service is created in a given subscription and can take
    a few minutes to complete on a subscription that's never used App Service before.

.PARAMETER ResourceGroup
    Name of the Azure resource group to create/use.

.PARAMETER SubscriptionId
    Azure subscription ID (or name) to deploy into. Optional - if supplied, the
    script runs `az account set --subscription` first so you don't have to switch
    subscriptions manually. If omitted, whatever subscription is currently active
    in the Azure CLI (`az account show`) is used.

.PARAMETER Location
    Azure region, e.g. "westus3", "westeurope". Every Azure resource is pinned to a
    physical datacenter region - there's no "default" region Azure can infer from
    your subscription, and the Free (F1) App Service plan tier is only available
    in a subset of regions, with a per-region/per-subscription quota on top of
    that (some regions, e.g. "eastus", may show a 0 quota for F1 on subscriptions
    that have never used it - see README for the quota-increase / region-switch
    workaround). Defaults to "westus3" (confirmed to have F1 quota available);
    override if you need data residency elsewhere or it's out of quota for you.
    List available regions with `az account list-locations -o table`.

.PARAMETER AppName
    Globally-unique Web App name. The app will be reachable at
    https://<AppName>.azurewebsites.net - pick something unlikely to collide,
    e.g. "whatdo-status-<yourname>".

.PARAMETER StatusIngestSecret
    Shared secret hook callers must send in the X-Status-Secret header.
    Passed as a SecureString so it is not echoed to the console or saved in
    shell history. You will be prompted for it if not supplied.

.PARAMETER PlanName
    App Service plan name. Defaults to "<AppName>-plan".

.PARAMETER Sku
    App Service plan SKU. Defaults to "F1" (Free tier).

.EXAMPLE
    ./provision-azure-resources.ps1 -ResourceGroup whatdo-rg     -AppName whatdo-status-app `
          -SubscriptionId <subscription-id>

    Switches to that subscription, prompts for the StatusIngestSecret, then
    creates/updates all resources in the default "westus3" region.
#>
param(
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [Parameter(Mandatory)] [string] $AppName,
    [Parameter(Mandatory)] [System.Security.SecureString] $StatusIngestSecret,
    [string] $SubscriptionId,
    [string] $Location = "westus3",
    [string] $PlanName = "$AppName-plan",
    [string] $Sku = "F1"
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI ('az') not found on PATH. Install it: https://learn.microsoft.com/cli/azure/install-azure-cli"
}

if ($SubscriptionId) {
    Write-Host "Switching to subscription '$SubscriptionId'..." -ForegroundColor Cyan
    az account set --subscription $SubscriptionId
    if ($LASTEXITCODE -ne 0) { throw "az account set failed with exit code $LASTEXITCODE" }
}

Write-Host "Logged in as:" -ForegroundColor Cyan
az account show --query "{name:name, id:id}" -o table
if ($LASTEXITCODE -ne 0) { throw "Not logged in to Azure CLI. Run 'az login' first." }

Write-Host "`nEnsuring the 'Microsoft.Web' resource provider is registered on this subscription (idempotent)..." -ForegroundColor Cyan
$providerState = az provider show --namespace Microsoft.Web --query registrationState -o tsv
if ($providerState -ne "Registered") {
    Write-Host "Not yet registered (state: $providerState). Registering - first-time registration on a subscription can take a few minutes..." -ForegroundColor Yellow
    az provider register --namespace Microsoft.Web
    if ($LASTEXITCODE -ne 0) { throw "az provider register failed with exit code $LASTEXITCODE" }

    do {
        Start-Sleep -Seconds 10
        $providerState = az provider show --namespace Microsoft.Web --query registrationState -o tsv
        Write-Host "  Microsoft.Web registration state: $providerState" -ForegroundColor DarkGray
    } while ($providerState -ne "Registered")
}
Write-Host "'Microsoft.Web' is registered." -ForegroundColor Green

$rgExists = az group exists --name $ResourceGroup
if ($rgExists -eq "true") {
    $rgLocation = az group show --name $ResourceGroup --query location -o tsv
    Write-Host "`nResource group '$ResourceGroup' already exists (in '$rgLocation') - reusing it as-is." -ForegroundColor Cyan
    Write-Host "  (A resource group's own location is fixed at creation, but the resources inside it, e.g. the App Service plan below, can still be created in a different region such as '$Location'.)" -ForegroundColor DarkGray
}
else {
    Write-Host "`nCreating resource group '$ResourceGroup' in '$Location'..." -ForegroundColor Cyan
    az group create --name $ResourceGroup --location $Location --output table
    if ($LASTEXITCODE -ne 0) { throw "az group create failed with exit code $LASTEXITCODE" }
}

Write-Host "`nCreating App Service plan '$PlanName' ($Sku, Linux) (idempotent)..." -ForegroundColor Cyan
az appservice plan create `
    --name $PlanName `
    --resource-group $ResourceGroup `
    --location $Location `
    --is-linux `
    --sku $Sku `
    --output table
if ($LASTEXITCODE -ne 0) { throw "az appservice plan create failed with exit code $LASTEXITCODE" }

Write-Host "`nCreating Web App '$AppName' (.NET 8 on Linux) (idempotent)..." -ForegroundColor Cyan
az webapp create `
    --name $AppName `
    --resource-group $ResourceGroup `
    --plan $PlanName `
    --runtime "DOTNETCORE:8.0" `
    --output table
if ($LASTEXITCODE -ne 0) { throw "az webapp create failed with exit code $LASTEXITCODE" }

Write-Host "`nEnforcing HTTPS-only..." -ForegroundColor Cyan
az webapp update --name $AppName --resource-group $ResourceGroup --https-only true --output none
if ($LASTEXITCODE -ne 0) { throw "az webapp update (https-only) failed with exit code $LASTEXITCODE" }

$bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($StatusIngestSecret)
try {
    $plainSecret = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
}
finally {
    [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

Write-Host "`nSetting StatusIngest secret app setting..." -ForegroundColor Cyan
az webapp config appsettings set `
    --name $AppName `
    --resource-group $ResourceGroup `
    --settings "StatusIngest__Secret=$plainSecret" `
    --output none
if ($LASTEXITCODE -ne 0) { throw "az webapp config appsettings set failed with exit code $LASTEXITCODE" }
Remove-Variable plainSecret -ErrorAction SilentlyContinue

Write-Host "`nDone. App URL: https://$AppName.azurewebsites.net" -ForegroundColor Green
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Run deploy/deploy.ps1 -ResourceGroup $ResourceGroup -AppName $AppName to publish the app." -ForegroundColor Yellow
Write-Host "  2. Update hooks/whatdo-status.json to point at https://$AppName.azurewebsites.net/api/status-event" -ForegroundColor Yellow
Write-Host "  3. Note: F1 (Free) tier has no 'Always On' - the app idles after ~20 min and cold-starts on the next request." -ForegroundColor Yellow
