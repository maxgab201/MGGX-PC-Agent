# MGGX PC Agent 1.1 pairing contract

Implements the contract authored by MGGX PC Control 2 alpha2 at
`MGGX-PC-Control/docs/PC_AGENT_PAIRING_V1.md`. This file must never drift from that one; if a field name,
status code, or validation rule changes, update both repositories together.

This lets the phone that stays at home (`MGGX PC Control 2`) pair itself with the PC Agent directly over
LAN or Tailscale, without the user ever copying an IP, port, MAC address, or token.

## Offer shown by Windows

MGGX PC Agent Control's "Vincular celular de casa" action asks the Windows Service (over a local Named
Pipe, `MGGXPCAgentPairingPipe`) to generate a pairing offer:

- A 32-byte, cryptographically random (`RandomNumberGenerator`) secret, Base64URL-encoded without padding
  (43 characters) — 256 bits of entropy.
- Single-use: the first successful claim consumes it atomically.
- Expires 10 minutes after generation.
- Generating a new offer immediately invalidates whatever offer was active before.
- A six-digit display code is shown next to the QR as a human verification aid only. It is never sent to
  or accepted by the Agent as a credential.

The offer is rendered as this QR payload:

```text
mggx://pc-agent/v1?host=<LAN-IP>&port=8766&secret=<base64url-43>&expires=<epoch-ms>
```

The QR never contains the permanent Agent Token, the Windows account password, or any Tailscale/Sunshine
credential. `<LAN-IP>` is the real physical LAN adapter's address (see "LAN detection" below), never a
virtual adapter's address.

The pairing session lives in the Windows Service, not in Control: closing Control does not cancel or lose
the offer. It stays valid until it is claimed, cancelled, regenerated, or it expires.

## Claim

```http
POST /api/v1/pair/claim
Content-Type: application/json
```

```json
{
  "protocolVersion": 1,
  "secret": "<single-use-secret>",
  "client": "mggx-pc-control-home"
}
```

This endpoint does **not** require a Bearer token — it is how the very first credential for a new phone is
created. It is bound to private networks only (see "Network restriction") and is independently rate
limited (10 requests/minute/IP) on top of the general API limiter.

Successful response (HTTP 200):

```json
{
  "ok": true,
  "protocolVersion": 1,
  "agentToken": "<new-permanent-agent-token>",
  "agentPort": 8766,
  "agentVersion": "1.1.0",
  "pcId": "main",
  "name": "MGGX PC",
  "lanIp": "192.168.1.20",
  "tailscaleIp": "100.64.10.20",
  "macAddress": "00:11:22:33:44:55",
  "broadcastAddress": "192.168.1.255"
}
```

`agentToken` is a brand-new, distinct 256-bit credential for this phone — never a previously issued token,
and never the legacy Agent Token. It authenticates immediately: no service restart is required. The Agent
never returns the same `agentToken` twice.

### Errors

| Status | Meaning |
|---|---|
| `400` | Malformed body, wrong `protocolVersion`, or wrong `client`. |
| `401` | Secret is invalid, expired, or already consumed. |
| `403` | Caller's IP is not on an allowed private network. |
| `429` | Rate limit exceeded (10/minute/IP). |
| `500` | Internal error (for example: no usable LAN adapter was detected). |

No error response leaks which of "wrong", "expired", or "already used" applied — Android treats all of
them as "generate a new code."

## Network restriction

The claim endpoint only accepts requests from:

- Loopback (used by local diagnostics/tests).
- RFC1918 LAN: `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`.
- Tailscale CGNAT range: `100.64.0.0/10`.

Any other source address gets `403`. The Agent never opens this — or any other — endpoint to the public
internet, and the installer never configures port forwarding or disables the Windows Firewall.

## Multi-token authentication

Every `/api/v1/*` route accepts **either**:

- The legacy single Agent Token (unchanged, still copyable from Control → Configuración → Avanzado for
  existing integrations), or
- Any active paired-device token issued by `/pair/claim`.

Each paired device gets its own distinct token. Only the SHA-256 hash of each paired token is persisted
(with metadata: id, client, display name, created/last-seen timestamps) — the plaintext is returned once,
at claim time, and never stored or logged. MGGX PC Agent Control's "Dispositivos vinculados" section lists
paired devices and can revoke one; revocation deauthorizes that token immediately and does not affect any
other paired device or the legacy token.

## LAN detection

`lanIp`, `macAddress`, and `broadcastAddress` come from the same physical adapter, chosen by:

1. Prefer an adapter with a default gateway.
2. Prefer Ethernet, then Wi-Fi, over other adapter types.
3. Exclude loopback, Tailscale, Hyper-V, WSL, VMware, VirtualBox, Docker, Bluetooth, tunnel/PPP adapters,
   and APIPA (`169.254.0.0/16`) addresses.

`broadcastAddress` is computed from the adapter's real subnet mask (not assumed to be `/24`). The adapter
can be overridden manually in Control → Configuración → Red if automatic detection picks the wrong one.

## Existing Agent API used right after pairing

- `GET /health` — unauthenticated, `uptimeSeconds` is the Agent **process** uptime.
- `GET /api/v1/status` — `Authorization: Bearer <agentToken>`.
- Power (`/api/v1/power/shutdown|restart|sleep|hibernate|lock`) and
  `/api/v1/services/sunshine/restart` — same Bearer auth, documented in `docs/RELAY_API.md`.
