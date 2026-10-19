# Deploying WhatDo.StatusService to Azure

These scripts never deploy anything on their own — you always trigger them,
either by running `deploy.ps1` yourself or by dispatching the GitHub Actions
workflow. Nothing here runs automatically on push.

## One-time setup (you run this manually)

1. Install the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli)
   and run `az login`.
2. Choose a globally-unique app name (it becomes `https://<name>.azurewebsites.net`),
   e.g. `whatdo-status-app`.
3. Run the provisioning script once, passing your subscription ID so the script
   switches to it for you (no need to run `az account set` separately):

   ```powershell
   ./deploy/provision-azure-resources.ps1 `
     -ResourceGroup whatdo-rg `
     -AppName whatdo-status-app `
     -SubscriptionId <subscription-id>
   ```

   You'll be prompted to enter the `StatusIngest` shared secret (used in the
   `X-Status-Secret` header hooks must send). This creates, idempotently:
   - The `Microsoft.Web` resource provider registration on the subscription, if
     not already registered (one-time per subscription; can take a few minutes
     the first time an App Service is created there — you'll see the script poll
     until it's ready)
   - A resource group (default region: `westus3` — override with `-Location` if
     you need a different one; list options with `az account list-locations -o table`).
     Note: the Free (F1) App Service tier has a per-region quota that can be 0
     on subscriptions that have never used it (commonly `eastus`); if plan
     creation fails with a quota error, either request a quota increase in the
     Azure Portal or try a different region.
   - A Linux App Service plan on the **F1 (Free)** tier
   - A Web App running .NET 8, HTTPS-only
   - The `StatusIngest__Secret` app setting

   Re-running this script is safe — every step is create-or-update.

## Deploying the app (repeatable — run manually or via CI)

```powershell
./deploy/deploy.ps1 -ResourceGroup whatdo-rg -AppName whatdo-status-app `
  -SubscriptionId <subscription-id>
```

This builds (`dotnet publish -c Release`), zips the output, and pushes it via
`az webapp deploy` (zip deploy) to the Web App created above. `-SubscriptionId`
is optional on both scripts — omit it if your Azure CLI is already pointed at
the right subscription.

## After the first deploy

1. Update `hooks/whatdo-status.json`: replace every
   `http://localhost:PORT/api/status-event` with
   `https://<your-app-name>.azurewebsites.net/api/status-event`, and update
   `X-Status-Secret` to match the secret you set during provisioning.
2. Because the endpoint is now `https://`, you can re-add a `PreToolUse` entry
   if you want (it's only required to use `https://`, which is now satisfied) —
   see the note in `hooks/README.md`.
3. Remove the `COPILOT_HOOK_ALLOW_LOCALHOST` requirement from your setup notes;
   it's only needed for `http://localhost` hooks.
4. **Known limitation (until Stage 2 lands):** the app still uses the in-memory
   instance store. The **F1 (Free)** tier has no "Always On" setting, so the app
   idles after ~20 minutes of inactivity and loses all instance state on the next
   cold start. This is expected until Stage 2 (Azure Table Storage persistence)
   replaces the in-memory store.

## Web Push notifications (Android / desktop)

Generate a VAPID key pair once: `npx web-push generate-vapid-keys`.

Set these app settings (App Service uses `__` for nesting); leave them unset to disable push:

```powershell
az webapp config appsettings set -g whatdo-rg -n whatdo-status-app --settings `
  Push__PublicKey="<public>" Push__PrivateKey="<private>" Push__Subject="mailto:you@example.com"
```

Locally use `dotnet user-secrets set "Push:PublicKey" "..." --project src\WhatDo.StatusService` (same for `Push:PrivateKey` and `Push:Subject`).

Open the site on the phone (Chrome, HTTPS) and tap **Enable notifications**. A notification is sent each time an instance transitions into `PendingInput`. Subscriptions are held in memory, so they are lost on restart or redeploy; each browser re-registers when the page is next opened.

## Troubleshooting

### `SSL: CERTIFICATE_VERIFY_FAILED: self-signed certificate in certificate chain`

This happens on corporate networks that run an SSL-inspecting proxy (common on
managed/work laptops). Azure CLI's bundled Python uses its own `certifi` CA
bundle instead of the Windows certificate store, so it doesn't trust the
proxy's self-signed root cert even though your browser (which does use the
Windows store) connects fine. It's an environment issue, not a script bug.

**Option A (preferred): `pip-system-certs`.** Install it into Azure CLI's own
Python interpreter so it trusts everything Windows trusts:

```powershell
& "C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\python.exe" -m pip install pip-system-certs
```

(64-bit installs may use `C:\Program Files\Microsoft SDKs\Azure\CLI2\python.exe`
instead — check which path exists on your machine.) Restart your terminal
afterward and re-run the script.

**Option B (fallback, if `python -m pip ...` itself crashes with an access
violation):** some locked-down/AV-monitored machines have a broken `pip`
inside the Azure CLI's bundled Python, so Option A isn't usable. Instead,
export your corporate root CA(s) from the Windows certificate store and point
`REQUESTS_CA_BUNDLE` at a copy of `certifi`'s bundle with those CAs appended,
for just the current session:

```powershell
$certifiBundle = "C:\Program Files\Microsoft SDKs\Azure\CLI2\Lib\site-packages\certifi\cacert.pem"
$combined = "$env:TEMP\corp-ca-bundle.pem"
Copy-Item $certifiBundle $combined -Force

# Replace with your org's actual root/intermediate CA subject name(s) —
# inspect `certmgr.msc` (Trusted Root Certification Authorities) to find them.
Get-ChildItem Cert:\LocalMachine\Root | Where-Object Subject -match "YourCorpCA" | ForEach-Object {
    $b64 = [Convert]::ToBase64String($_.RawData, 'InsertLineBreaks')
    Add-Content -Path $combined -Value "-----BEGIN CERTIFICATE-----`n$b64`n-----END CERTIFICATE-----"
}

$env:REQUESTS_CA_BUNDLE = $combined
.\deploy\deploy.ps1 -ResourceGroup whatdo-rg -AppName whatdo
```

This only needs to be set once per terminal session (it doesn't persist across
new PowerShell windows). It makes `az`'s Python trust the proxy's cert without
touching the broken `pip` install.

### App page loads blank in the browser, but the app seems to be running

Same root cause as above: a corporate SSL-inspecting proxy. Even once the app
is deployed and healthy (check **Log stream** in the Portal — you should see
`Now listening on: ...` and no errors), the proxy can corrupt the HTTPS
chunked-transfer response in transit, so the browser renders nothing even
though the server responded with a valid `200 OK`.

To confirm this is the cause rather than an app/deployment problem:
- Load the site from a connection that bypasses the proxy (e.g. your phone's
  mobile data, not wifi). If it renders fine there, the app is working and
  this is purely a local network issue.
- From a machine behind the proxy, `curl -k` against the URL will typically
  fail with `curl: (56) Malformed encoding found in chunked-encoding` partway
  through the response, confirming the stream is being corrupted in transit
  (not a `curl`/app bug).

There's no app-side fix for this — it's your network's TLS inspection
breaking the response stream. Ask your network/IT team to allowlist
`*.azurewebsites.net` for TLS inspection bypass, or just verify/test the site
from a connection that doesn't go through that proxy.

## Deploying via GitHub Actions

`.github/workflows/deploy.yml` wraps `deploy.ps1` in a workflow you can dispatch
manually from the Actions tab (`workflow_dispatch`). It does **not** run on
every push — you decide when to deploy.

To use it, once this repo has a GitHub remote:

1. Create a service principal Actions can log in with:

   ```powershell
   az ad sp create-for-rbac `
     --name "whatdo-status-deploy" `
     --role contributor `
     --scopes /subscriptions/<subscription-id>/resourceGroups/whatdo-rg `
     --sdk-auth
   ```

   Save the JSON output as the repository secret `AZURE_CREDENTIALS`
   (Settings → Secrets and variables → Actions → New repository secret).

2. Add two repository **variables** (not secrets, they're not sensitive):
   `AZURE_RESOURCE_GROUP` = `whatdo-rg`, `AZURE_WEBAPP_NAME` = `whatdo-status-app`.
3. Run the workflow from the Actions tab ("Run workflow").
