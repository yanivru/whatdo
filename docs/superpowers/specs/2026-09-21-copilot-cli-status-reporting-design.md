# WhatDo — Copilot CLI Status Reporting Design

## Problem

Multiple GitHub Copilot CLI instances may be running at once (different repos/machines). We want a way to know, at a glance, whether each instance is `working`, `idle`, or `pending_input` (waiting on the user), from three kinds of clients: a web dashboard, an ESP32 device, and (later) an Android app.

## Approach

Copilot CLI supports **HTTP hooks**: JSON payloads POSTed to a URL at lifecycle events (session start/end, prompt submitted, tool use, notifications, turn end). We use these hooks — no wrapper script or CLI modification needed — to report status to a small always-on backend, which republishes status over **MQTT** (HiveMQ Cloud Free tier) so that any number of clients (web, ESP32, future Android) get instant, poll-free updates via MQTT's retained-message feature.

## Architecture

One ASP.NET Core app, **WhatDo.StatusService**, deployed to **Azure App Service Free (F1) tier**, doing two jobs:

1. **Ingest API** — `POST /api/status-event`. Copilot CLI hooks POST here, secured by a shared-secret header (`X-Status-Secret`, expanded from an env var via the hook's `allowedEnvVars`).
2. **Blazor Server dashboard** ("the site") — served by the same app. Because Blazor Server keeps a live SignalR connection to the browser already, it can push MQTT-driven updates instantly with no client-side MQTT/JS needed.

A background hosted service inside the app holds one persistent **MQTTnet** connection to **HiveMQ Cloud (Free tier: 100 connections, 10GB/month, username+password over TLS, no cert provisioning)** and:
- Publishes a **retained** MQTT message per CLI instance to `whatdo/status/<repo-folder>/<shortSessionId>`, and a retained aggregate to `whatdo/status/_aggregate`, whenever a hook event arrives.
- Runs a periodic sweep (~every 60s) that marks instances "offline" (clears their retained topic) if no event has arrived for ~3 minutes — this catches CLI sessions that crash/are killed without ever firing `SessionEnd`.

Instance state (status, last-seen, name, cwd) is persisted in **Azure Table Storage** so it survives app restarts.

The ESP32 and any future Android app connect **directly to HiveMQ** (not through our app). Retained messages mean a freshly-connecting/rebooting client gets the current status immediately on subscribe — no polling, no REST calls needed for status display.

```
Copilot CLI (N instances)
   │  HTTP hook POST (SessionStart, UserPromptSubmit, PreToolUse,
   │  PostToolUse, PostToolUseFailure, Notification, Stop, SessionEnd)
   ▼
WhatDo.StatusService (Azure App Service F1)
   ├─ Ingest API ─┬─> Azure Table Storage (instance state)
   │              └─> MQTTnet client ──publish (retained)──> HiveMQ Cloud
   ├─ 60s staleness sweep ─(same path)─┘
   └─ Blazor Server dashboard <──subscribe── (same MQTTnet client)

HiveMQ Cloud (broker)
   ├──subscribe(retained)── Web dashboard (via app, above)
   ├──subscribe(retained)── ESP32 (direct, WiFiClientSecure + PubSubClient)
   └──subscribe(retained)── Android app (direct, later — out of scope for now)
```

## Status Mapping

Hooks are configured using **PascalCase event names** (`SessionStart`, `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `PostToolUseFailure`, `Notification`, `Stop`, `SessionEnd`) so every payload includes a `hook_event_name` field (VS Code–compatible format), letting a single ingest endpoint dispatch on event type.

| Event | `notification_type` (if applicable) | → Status |
|---|---|---|
| `SessionStart` | — | `idle` (session open, nothing submitted yet) |
| `UserPromptSubmit` | — | `working` |
| `PreToolUse` / `PostToolUse` / `PostToolUseFailure` | — | `working` |
| `Notification` | `permission_prompt`, `elicitation_dialog` | `pending_input` |
| `Notification` | `agent_idle`, `shell_completed`, `agent_completed`, `shell_detached_completed` | `idle` |
| `Stop` | — | `idle` (turn finished, waiting for next prompt) |
| `SessionEnd` | — | instance removed (retained topic cleared, row deleted) |
| (60s sweep, no event for ~3 min) | — | instance removed (treated as crashed) |

`errorOccurred` is not mapped to a status change (logged only); it doesn't fit the 3-state model cleanly and isn't required for the core use case.

**Instance ID**: sanitized last path segment of `cwd` (lowercase, non-alphanumeric → `-`) + first 8 chars of `sessionId`, e.g. `whatdo-a1b2c3d4`. This is also the display name (folder-derived, zero-config).

**Topics**:
- Per instance: `whatdo/status/<folder>/<shortSessionId>` → `{ "status": "...", "name": "...", "cwd": "...", "lastEvent": "...", "updatedAt": "..." }`
- Aggregate: `whatdo/status/_aggregate` → `{ "status": "...", "workingCount": n, "idleCount": n, "pendingCount": n, "total": n }`

**Aggregation rule**: worst-case wins — `pending_input` > `working` > `idle` — across all currently-known (non-stale) instances. Empty set → aggregate status `idle`, `total: 0`.

## Components

- `src/WhatDo.StatusService/` — ASP.NET Core app (ingest API + Blazor Server dashboard + hosted services)
  - `Endpoints/StatusEventEndpoint.cs` — minimal API endpoint, secret validation, dispatch by `hook_event_name`
  - `Status/StatusMapper.cs` — pure function: hook payload → `InstanceStatus` (`Working`/`Idle`/`PendingInput`) or `null` (remove)
  - `Status/InstanceStore.cs` — Azure Table Storage–backed store (get/upsert/remove/list, last-seen tracking)
  - `Status/AggregateCalculator.cs` — pure function: instance list → aggregate status/counts
  - `Mqtt/IMqttPublisher.cs` + `HiveMqPublisher.cs` (MQTTnet-based) — publish retained messages; test double for unit tests
  - `Mqtt/StaleInstanceSweepService.cs` — `BackgroundService` running the 60s sweep
  - `Pages/Dashboard.razor` — Blazor Server page subscribing to `whatdo/status/#`, rendering per-instance list + aggregate badge
- `hooks/whatdo-status.json` — Copilot CLI hook config template (drop into `~/.copilot/hooks/`), posts to the deployed ingest endpoint for all 8 events listed above
- `firmware/esp32/whatdo_status/whatdo_status.ino` — reference Arduino sketch: WiFiClientSecure + PubSubClient, subscribes to `whatdo/status/_aggregate` (and optionally `whatdo/status/#`) to drive an LED/small display
- `src/WhatDo.StatusService.Tests/` — unit + integration tests

## Error Handling

- Ingest endpoint validates the shared-secret header (401 on missing/wrong secret); returns 200 quickly after enqueuing the MQTT publish (fire-and-forget) so slow/failed publishes never block or fail the CLI hook call. HTTP hooks other than `PreToolUse` are fail-open in the CLI anyway, so this endpoint being briefly unavailable never breaks the user's CLI session.
- Malformed or unrecognized payloads are logged and ignored (200 OK) rather than erroring.
- MQTTnet auto-reconnects to HiveMQ with backoff on connection loss. If a publish fails, the next hook event (or the 60s sweep) self-corrects state — no message queue/retry infrastructure needed.
- Table Storage failures on a single event are logged; the sweep will reconcile on the next pass.

## Testing

- Unit tests: `StatusMapper` (event → status for every event/notification_type combination), `AggregateCalculator` (priority ordering, empty set), instance-ID sanitization.
- Integration tests: POST sample hook payloads (one per event type) to the ingest endpoint against a fake `IMqttPublisher`, assert correct topic, payload shape, and `retain: true`.
- Manual end-to-end test: real Copilot CLI hook config pointing at the deployed Azure endpoint, verified by subscribing an MQTT client (e.g., MQTT Explorer) to the HiveMQ topics while running a live CLI session through prompts/tool calls/permission prompts.

## Out of Scope (for this spec)

- Android app (explicitly deferred — "maybe later")
- Authentication/authorization for MQTT subscribers beyond HiveMQ's shared broker credentials (fine for a personal/hobby setup; revisit if multi-user)
- Historical status logging/analytics (only current status is tracked)

## Open Items for Implementation Planning

- Exact HiveMQ Cloud free-tier account/cluster provisioning steps (manual, one-time)
- Azure App Service + Table Storage resource provisioning (manual or IaC — decide during planning)
- Final ESP32 hardware/display choice (LED vs. small OLED) — LED-only (aggregate topic) is the MVP; richer display is a stretch goal
