# WhatDo status hooks (Stage 1 — local manual testing)

## One-time setup

1. Run the app: `dotnet run --project src/WhatDo.StatusService/WhatDo.StatusService.csproj`
2. Note the `http://localhost:PORT` URL from the console output (for example
   `http://localhost:5276` — the port is randomly assigned by the project template
   and may differ on your machine).
3. Copy `hooks/whatdo-status.json` into `~/.copilot/hooks/whatdo-status.json` (or
   `%USERPROFILE%\.copilot\hooks\whatdo-status.json` on Windows), replacing every
   `http://localhost:PORT` with the actual port from step 2.
4. Set `COPILOT_HOOK_ALLOW_LOCALHOST=1` in the environment before launching `copilot`
   (required for non-TLS localhost HTTP hooks). On Windows you can persist this once
   for all future terminals with `setx COPILOT_HOOK_ALLOW_LOCALHOST 1` — open a **new**
   terminal afterwards for it to take effect.
5. **Hook configs are only read when the CLI starts.** If `copilot` was already running
   before you added/edited the hook file, quit it (`/exit` or `ctrl+d`) and start a new
   session. Inside the new session, run `/env` and confirm `whatdo-status.json` /
   its events are listed under loaded hooks — if not, the file wasn't picked up
   (check the path and that it's valid JSON).

> Note: `PreToolUse` is intentionally not included in this template — the hooks
> reference requires `https://` for `PreToolUse` (and `permissionRequest`) because
> their response can grant tool permissions, so a plain `http://localhost` entry for
> that event is silently dropped. `PendingInput` status still works via the
> `Notification` hook (`notification_type: "permission_prompt"`).

## Manual verification without the CLI (fastest check)

With the app running, POST a sample event directly:

```powershell
Invoke-RestMethod -Method Post -Uri "http://localhost:PORT/api/status-event" `
  -Headers @{ "X-Status-Secret" = "dev-secret-change-me" } `
  -ContentType "application/json" `
  -Body '{"hook_event_name":"UserPromptSubmit","session_id":"a1b2c3d4e5f6","cwd":"C:\\repos\\whatdo"}'
```

Open `http://localhost:PORT/` in a browser — within 2 seconds you should see a row:
`whatdo | Working | UserPromptSubmit | <time>`.

Then POST a `Stop` event for the same session and confirm the row updates to `Idle`,
and a `SessionEnd` event and confirm the row disappears.

## Manual verification with the real Copilot CLI

1. With the hook file installed (see setup above) and the app running, start a new
   `copilot` session in any repo.
2. Watch the dashboard page — a row should appear shortly after session start (`Idle`).
3. Submit a prompt — the row should flip to `Working`.
4. When Copilot asks for a tool permission, the row should flip to `PendingInput`.
5. When the turn finishes, the row should return to `Idle`.
6. Exit the CLI session (`/exit` or `ctrl+d`) — the row should disappear.
