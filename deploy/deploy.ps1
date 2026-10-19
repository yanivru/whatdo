<#
.SYNOPSIS
    Builds and deploys WhatDo.StatusService to an existing Azure App Service.

.DESCRIPTION
    Runs `dotnet publish`, zips the output, and deploys it via `az webapp deploy`
    (zip deploy). Assumes the target Resource Group / App Service already exist - 
    run provision-azure-resources.ps1 once beforehand if they don't.

    This script never runs itself: you run it manually, or a GitHub Actions
    workflow runs it on your behalf (see .github/workflows/deploy.yml).

.PARAMETER ResourceGroup
    Name of the existing Azure resource group containing the Web App.

.PARAMETER AppName
    Name of the existing Azure Web App to deploy to.

.PARAMETER SubscriptionId
    Azure subscription ID (or name) to deploy into. Optional - if supplied, the
    script runs `az account set --subscription` first. If omitted, whatever
    subscription is currently active in the Azure CLI is used.

.PARAMETER Configuration
    Build configuration. Defaults to "Release".

.PARAMETER ProjectPath
    Path to the WhatDo.StatusService.csproj to publish. Defaults to the project
    in this repository.

.EXAMPLE
    ./deploy.ps1 -ResourceGroup whatdo-rg     -AppName whatdo-status-app `
          -SubscriptionId <subscription-id>
#>
param(
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [Parameter(Mandatory)] [string] $AppName,
    [string] $SubscriptionId,
    [string] $Configuration = "Release",
    [string] $ProjectPath = "$PSScriptRoot/../src/WhatDo.StatusService/WhatDo.StatusService.csproj"
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI ('az') not found on PATH. Install it: https://learn.microsoft.com/cli/azure/install-azure-cli"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK ('dotnet') not found on PATH."
}

if ($SubscriptionId) {
    Write-Host "Switching to subscription '$SubscriptionId'..." -ForegroundColor Cyan
    az account set --subscription $SubscriptionId
    if ($LASTEXITCODE -ne 0) { throw "az account set failed with exit code $LASTEXITCODE" }
}

$publishDir = Join-Path $PSScriptRoot "publish"
$zipPath = Join-Path $PSScriptRoot "publish.zip"

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host "Publishing $ProjectPath ($Configuration)..." -ForegroundColor Cyan
dotnet publish $ProjectPath -c $Configuration -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "`nZipping publish output..." -ForegroundColor Cyan
# Compress-Archive stores subfolder paths with backslashes on Windows, which
# Linux App Service can't extract (rsync then fails with "Invalid argument").
# ZipFile.CreateFromDirectory always uses forward slashes in zip entries.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipPath)

Write-Host "`nDeploying to App Service '$AppName' in resource group '$ResourceGroup'..." -ForegroundColor Cyan
az webapp deploy `
    --resource-group $ResourceGroup `
    --name $AppName `
    --src-path $zipPath `
    --type zip
if ($LASTEXITCODE -ne 0) { throw "az webapp deploy failed with exit code $LASTEXITCODE" }

Write-Host "`nDeployed. App URL: https://$AppName.azurewebsites.net" -ForegroundColor Green

Remove-Item $publishDir -Recurse -Force
Remove-Item $zipPath -Force
