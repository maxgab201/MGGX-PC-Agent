# MGGX Relay → PC Agent API v1

This is the authoritative contract for the Android relay (`relay.py`). The relay is the only normal client. The Windows Agent listens on TCP `8766` by default; discovery listens on UDP `8767`.

## Connection and authentication

- Base URL: `http://<pc-lan-or-tailscale-ip>:8766`
- Agent token: independent 256-bit base64url secret copied from MGGX PC Agent Control, or a paired-device
  token issued by `POST /api/v1/pair/claim` (see `docs/PAIRING_API_V1.md`). Both keep working side by side.
- Send `Authorization: Bearer <AGENT_TOKEN>` to every `/api/v1/*` route.
- Never send the Cel 1 → Relay token unless it was explicitly configured as the Agent token too.
- Never log the token or Authorization header. Do not follow redirects with Authorization.
- Suggested connect timeout: 1.5 s; read timeout: 3 s; power request timeout: 3 s.
- Retry idempotent `GET` requests only. Do not automatically retry accepted power `POST`s.

## Online state

`GET /health` is unauthenticated. HTTP 200 with `ok: true` means the PC is `ONLINE`. Connection failure/timeout means `OFFLINE` (or keep `WAKING` until the relay's wake deadline expires).

```json
{"ok":true,"service":"mggx-pc-agent","apiVersion":1,"agentVersion":"1.1.0","uptimeSeconds":1234}
```

`uptimeSeconds` here is the Agent **process**'s own uptime, not the Windows machine's uptime (that distinction matters for restart detection).

## Status

`GET /api/v1/status` → 200:

```json
{
  "ok": true,
  "apiVersion": 1,
  "agentVersion": "1.1.0",
  "pc": {"state":"online","machineName":"MAX-PC","uptimeSeconds":12345},
  "windows": {"version":"Microsoft Windows ...","locked":false},
  "sunshine": {"installed":true,"running":true,"ip":null},
  "tailscale": {"installed":true,"running":true,"ip":"100.x.x.x"},
  "power": {"sleepSupported":true,"hibernateSupported":true}
}
```

## Actions

All routes are `POST`, require the Bearer token, and have an empty request body. Treat `200` and `202` as
equally successful — both mean "ok": Windows genuinely accepted the action, never a fabricated success. A
`5xx`/exception response instead means the action failed and nothing was scheduled.

| Route | Success | State | Unsupported/missing |
|---|---:|---|---|
| `/api/v1/power/shutdown` | 200 (verified) | `shutting_down` | — |
| `/api/v1/power/restart` | 200 (verified) | `restarting` | — |
| `/api/v1/power/sleep` | 200 or 202 | `sleeping` | 409 `sleep_not_available` |
| `/api/v1/power/hibernate` | 200 or 202 | `hibernating` | 409 `hibernate_not_available` |
| `/api/v1/power/lock` | 200 (verified) | `locking` | — |
| `/api/v1/services/sunshine/restart` | 202 | `restarting` | 409 `sunshine_not_installed` |

Shutdown/restart/lock are confirmed synchronously — Windows genuinely accepted the transition before the
response is sent. Sleep/hibernate cannot be awaited to completion (the call only returns once the machine
wakes back up), so a real, immediate rejection (policy disallows it, unsupported) still surfaces as an
error; otherwise the response means the transition is genuinely under way, not merely requested.

Success response: `{"ok":true,"state":"shutting_down"}`. Error response: `{"ok":false,"error":"hibernate_not_available"}`.

## HTTP errors

- `401 unauthorized`: missing/wrong Agent token. Do not retry; mark Agent authentication misconfigured.
- `409 conflict`: action is unavailable on that PC. Surface its stable `error` string.
- `429 too many requests`: back off for at least 5 seconds.
- `5xx`: Agent fault; retry only health/status with bounded exponential backoff.
- Connection refused/timeout/DNS error: Agent is offline/unreachable; do not report an authenticated API error.

## Discovery

Broadcast UTF-8 `MGGX_DISCOVER_V1` to UDP port `8767` on the LAN. An enabled Agent answers without a token:

```json
{"service":"mggx-pc-agent","apiVersion":1,"port":8766,"machineName":"MAX-PC"}
```

Validate sender address, `service`, `apiVersion`, port range, and then verify `/health`. Discovery is optional; a configured IP remains supported.

## Wake flow

After sending WOL, poll `/health` every 1–2 seconds with a practical overall deadline (for example 120 seconds). Transition `WAKING → ONLINE` only after health succeeds. Then fetch authenticated status. After shutdown returns success, poll health until it stops responding and transition to `OFFLINE`.
