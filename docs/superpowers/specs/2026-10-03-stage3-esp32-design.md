# WhatDo — Stage 3: ESP32 Status Indicator Design (DRAFT — needs confirmation)

> **Status: DRAFT.** This document captures a recommended design based on the original design
> spec's stated MVP direction (`docs/superpowers/specs/2026-09-21-copilot-cli-status-reporting-design.md`,
> "Open Items" section). Several decisions below are **assumptions, not confirmed choices** — see
> "Open Questions Blocking Approval" at the end. Do not implement from this document until those
> are resolved and the doc is updated to "Status: APPROVED".

## Problem

Stage 2 made WhatDo's aggregate and per-instance status available as retained MQTT messages on a
real broker (EMQX Cloud Serverless). Stage 3 builds a small physical device — an ESP32 — that
subscribes directly to those topics and shows status at a glance (e.g. on a desk), with zero
dependency on the web dashboard or polling.

## Approach (assumed MVP, per original spec)

The ESP32 connects to WiFi, opens a single persistent TLS MQTT connection directly to the EMQX
broker (same pattern as any other subscriber — no special API, no auth beyond the same
username/password already used by the status service), and subscribes to `whatdo/status/_aggregate`
(retained). On receipt, it parses the JSON and drives an LED based on `status`:

- `working` → solid/pulsing one color (e.g. blue)
- `pending_input` → a distinct, attention-grabbing color (e.g. yellow/amber), since this means "a
  human needs to respond"
- `idle` → a calm color (e.g. green) or off
- No retained message yet / broker unreachable → a distinct "unknown" state (e.g. slow white pulse)
  so the device never silently shows stale info as if it were current

Because the aggregate topic is retained, the ESP32 gets the correct status immediately on
boot/reconnect — no polling, no REST calls, matching the whole point of Stage 2.

Per-instance detail (`whatdo/status/<folder>/<shortSessionId>`) is **out of scope for the MVP** —
the original spec explicitly calls out aggregate-only LED as the MVP, with per-instance / richer
display as a stretch goal. This keeps the firmware simple (one topic, one small JSON payload, no
need to track a dynamic set of instances or cycle a display).

## Components

- `firmware/esp32/whatdo_status/` — new top-level firmware project (this repo currently has no
  hardware/firmware folder; this establishes the convention, matching the path already named in
  the original design spec)
  - `whatdo_status.ino` (or `src/main.cpp` if PlatformIO — see open question) — WiFi connect, TLS
    MQTT client (library: PubSubClient or a TLS-capable alternative — see open question), JSON
    parse (ArduinoJson), LED driver
  - `config.h.example` — WiFi SSID/password, MQTT host/port/username/password placeholders (never
    commit real credentials; `config.h` itself gitignored)
  - `README.md` — wiring diagram, parts list, flashing instructions
- No changes needed to `WhatDo.StatusService` — the ESP32 is a pure MQTT subscriber, consistent
  with the existing architecture ("ESP32 and any future Android app connect directly to HiveMQ/EMQX,
  not through our app").

## Data Flow

```
EMQX Cloud (broker)
   │  retained: whatdo/status/_aggregate
   │  { "status": "working"|"idle"|"pending_input", "workingCount": n, ... }
   ▼
ESP32 (WiFiClientSecure + MQTT client, subscribe on boot)
   ├─ on message / on (re)connect → parse JSON → map status → drive LED
   └─ on disconnect → LED shows "unknown" state until reconnected
```

## Error Handling

- WiFi or MQTT connection loss: standard reconnect-with-backoff loop (mirrors the service-side
  `MqttConnectionService` pattern conceptually, much simpler since there's only one subscription).
- No message received yet (e.g., fresh boot before first retained delivery): treat as "unknown",
  not "idle" — avoids showing a misleading default.
- Malformed/unexpected JSON: log via `Serial`, keep last-known-good LED state rather than crashing.

## Testing

- No automated tests (firmware, hardware-dependent) — manual verification only:
  1. Flash the device, confirm WiFi connects (watch `Serial` monitor).
  2. Confirm MQTT connects and the LED immediately reflects current aggregate status on boot
     (retained message delivery).
  3. Drive a real Copilot CLI session through Working → Idle → Pending-input (permission prompt)
     → confirm the LED changes within ~1-2 seconds each time.
  4. Power-cycle the device mid-"working" — confirm it comes back showing "working" immediately,
     not "idle"/"unknown" (this is the core retained-message value prop).
  5. Kill the broker connection (e.g. temporarily block WiFi) — confirm "unknown" state appears,
     then clears once reconnected.

## Out of Scope (for Stage 3 MVP)

- Per-instance display / cycling through multiple sessions
- OLED/richer display (explicitly a stretch goal per original spec)
- Android app
- Enclosure/physical design beyond "breadboard + LED is fine for v1"

## Open Questions Blocking Approval

These require information only you have — this draft cannot be approved/implemented until
answered (asked via `ask_user` but you were unavailable to respond in real time):

1. **Hardware you own** — what ESP32 board, and what LED/display hardware (if any) do you already
   have on hand? (e.g. plain ESP32 DevKit + single RGB LED + resistors, or something fancier like a
   NeoPixel ring, or nothing yet and parts need to be identified/bought.)
2. **Display choice** — confirm single/simple RGB LED (assumed MVP above) vs. an addressable LED
   (WS2812/NeoPixel, allows color + brightness/animation effects cheaply) vs. jumping straight to a
   small OLED (more info, more wiring/code complexity) vs. "not sure, want a recommendation" (my
   recommendation, if asked: start with a single RGB LED or one WS2812 — cheapest, simplest code,
   proves the whole pipeline; OLED can be a fast follow once the MVP works).
3. **Scope** — aggregate-only (assumed MVP above) vs. also cycling per-instance statuses on the
   same device.
4. **Firmware toolchain** — Arduino IDE (simpler for a single sketch, less repo tooling) vs.
   PlatformIO (better for VS Code/CLI workflow, consistent with how the rest of this repo is
   developed, easier dependency pinning) vs. no preference (my recommendation: PlatformIO, for
   consistency with the CLI-driven workflow already used throughout this project).

Once these are answered, this document should be updated (hardware/display/scope/toolchain
sections finalized, "Status: DRAFT" → "Status: APPROVED"), then the `writing-plans` skill should be
used to produce a task-by-task implementation plan, the same way Stage 1 and Stage 2 were.
