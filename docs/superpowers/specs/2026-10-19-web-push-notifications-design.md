# Web Push notifications for PendingInput — Design

## Goal
When a Copilot CLI instance enters `PendingInput`, the WhatDo site sends a Web Push notification so an Android phone is alerted even when the screen is locked and the tab is closed.

## Approach
Standard Web Push (PWA service worker + VAPID) using the `WebPush` NuGet package. Requires HTTPS (already provided by the Azure deployment; `localhost` is allowed for development). Android Chrome supports it from the browser; installing the site to the home screen is optional but more reliable.

## Components

### Client (`src/WhatDo.StatusService/wwwroot`)
- `manifest.webmanifest` — name, icons (reuse `favicon.png`), `display: standalone`; linked from `App.razor`.
- `sw.js` — handles `push` (shows notification from payload `title`/`body`, `tag` = instance id so repeats replace) and `notificationclick` (focuses an existing window or opens `/`).
- `push.js` — registers the service worker, requests permission, calls `pushManager.subscribe` with the VAPID public key from `GET /api/push/public-key`, POSTs the subscription to `/api/push/subscribe`.
- `Home.razor` — "Enable notifications" / "Disable notifications" button plus status text (unsupported, denied, enabled).

### Server (`src/WhatDo.StatusService/Push`)
- `PushOptions` — bound to section `Push`: `PublicKey`, `PrivateKey`, `Subject` (mailto). Push is disabled (endpoints return 404, notifier no-ops) when keys are empty. Keys come from user secrets / app settings, never committed.
- `IPushSubscriptionStore` + `InMemoryPushSubscriptionStore` — keyed by endpoint URL; capped at 50 subscriptions.
- `IPushSender` + `WebPushSender` — thin wrapper around `WebPushClient`; returns a result that flags expired subscriptions (HTTP 404/410).
- `PushNotifier` — `NotifyPendingInputAsync(instanceId, name)`; sends to all subscriptions, removes expired ones, logs and swallows other failures.
- `PushEndpoints` — `GET /api/push/public-key`, `POST /api/push/subscribe`, `POST /api/push/unsubscribe`.

## Data flow
1. In `StatusEventEndpoints` `UpdateStatus` branch, read the previous status via `IInstanceStore` before `Upsert`.
2. After `Upsert`, if the new status is `PendingInput` and the previous status was not (or the instance was new), call `PushNotifier.NotifyPendingInputAsync`.
3. Notifier sends payload `{ title: "Copilot needs input", body: "<name> is waiting for you", tag: instanceId }`.
4. Service worker displays it; tapping opens the status page.

`IInstanceStore` gains `TryGet(instanceId, out InstanceStatusInfo)` (or the transition is detected inside the endpoint from `GetAll()`) so the previous status is read safely without throwing on concurrent removal.

## Error handling
- Push failures never fail the hook request (matches existing "never fail the CLI hook" rule); the notifier is awaited inside try/catch.
- Expired subscriptions are removed; other errors are logged.
- Malformed subscribe bodies return 400.

## Security
- VAPID private key stays server-side. `/api/status` is already public, so subscribe is unauthenticated, but the subscription cap limits abuse. Notification text contains only the project folder name.

## Limitations
- The in-memory store loses subscriptions on app restart or redeploy; the page re-subscribes on next visit (the client re-POSTs its existing subscription on load). Persistent storage is out of scope.
- Notifications fire only on transition into `PendingInput`, not on every event.

## Testing
- `PushNotifierTests`: sends on transition, no send when already pending, removes subscriptions on 410, swallows sender exceptions (using a fake `IPushSender`).
- `StatusEventEndpointTests`: extended to verify the notifier is triggered only on transition.
- `PushEndpointsTests`: subscribe/unsubscribe/public-key, 400 on malformed body, disabled when keys are absent, subscription cap.
- Manual: Android Chrome over HTTPS, lock the phone, trigger a Notification hook.

## Out of scope
iOS support, persistent subscriptions, per-instance notification settings, notification actions.
