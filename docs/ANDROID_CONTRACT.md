# Relay → Android contract

Android continues to communicate only with MGGX Relay on port `8765`. It does not need the Agent IP, Windows token, Windows service name, or implementation details.

The Relay should normalize Agent data into its existing status response:

```json
{
  "pc": {
    "state": "offline|waking|online|shutting_down|restarting|sleeping|hibernating|locking|error",
    "machineName": "MAX-PC",
    "uptimeSeconds": 12345
  },
  "agent": {"reachable":true,"authenticated":true,"version":"1.0.0","apiVersion":1},
  "windows": {"version":"...","locked":false},
  "sunshine": {"installed":true,"running":true},
  "tailscale": {"installed":true,"running":true,"ip":"100.x.x.x"},
  "power": {"sleepSupported":true,"hibernateSupported":true}
}
```

Relay owns WOL, polling, Agent authentication, error translation, and IP/discovery. Android sends the existing explicit actions to Relay. Relay forwards them to the Agent and preserves meaningful errors (`agent_unauthorized`, `agent_unreachable`, `hibernate_not_available`, `sunshine_not_installed`, `rate_limited`).

Android must never receive or persist the Agent token. It may display Agent reachability/authentication diagnostics without exposing secrets. `ONLINE` is authoritative only when Relay successfully receives Agent `/health`.
