# MGGX PC Agent

Native Windows companion for MGGX PC Control. It provides an authenticated, explicit-action API, starts
automatically with Windows, monitors Sunshine and Tailscale, and includes a compact WinUI 3 control panel.
Version 1.1 adds automatic QR pairing so the phone that stays at home (**MGGX PC Control 2**) can pair
itself directly with the Agent — no IP, port, MAC address, or token ever needs to be copied by hand.

```
MGGX PC Control 2 (phone away)  --Tailscale-->  MGGX PC Control 2 (phone at home)  --LAN-->  MGGX PC Agent (Windows + Sunshine + Tailscale)
```

## Pairing a new phone

1. Install MGGX PC Agent, then set up Tailscale and Sunshine.
2. Open MGGX PC Agent Control and tap **"Vincular celular de casa"**.
3. Scan the QR code from MGGX PC Control 2.

The QR carries a single-use, 10-minute secret — never the permanent Agent Token — and the paired phone
gets its own distinct 256-bit credential. See [`docs/PAIRING_API_V1.md`](docs/PAIRING_API_V1.md) for the
full contract shared with MGGX PC Control 2.

## Components

- `MGGX.PCAgent.Service.exe`: low-overhead Windows Service and HTTP/UDP host. Owns the pairing session so
  it survives Control being closed.
- `MGGX.PCAgent.exe`: native WinUI 3 status/pairing/configuration panel. Closing it does not stop the
  service; it talks to the service over a local Named Pipe for pairing administration.
- Inno Setup installer: registers delayed-auto startup, recovery actions, and restricted Windows Firewall rules.

Defaults: service `MGGXPCAgent`, API `http://0.0.0.0:8766`, discovery UDP `8767`, API v1. Data and 7-day rolling logs live in `%ProgramData%\MGGX\PC-Agent`.

Security: legacy 256-bit Agent Token plus per-device paired tokens, all protected with Windows DPAPI
(`LocalMachine`) and an ACL limited to SYSTEM/Administrators; only SHA-256 hashes of paired tokens are ever
persisted; constant-time token comparison; the pairing claim endpoint is restricted to loopback/LAN/Tailscale
origins with its own rate limit; authenticated power/service actions verified before responding (never a
fabricated success); fixed-window rate limiting; no shell, command execution, uploads, monitor control,
camera, WebView, Chromium, or cloud dependency.

## Build

The supported production build runs on `windows-latest`:

```powershell
choco install innosetup -y
./scripts/build.ps1
```

Output: `artifacts/MGGX-PC-Agent-Setup-x64.exe`, portable ZIP, and `SHA256SUMS.txt`. Tests use fake power operations and never shut down the runner. See [Pairing API](docs/PAIRING_API_V1.md), [Relay API](docs/RELAY_API.md), and [Android contract](docs/ANDROID_CONTRACT.md).

## Compatibility

Windows 10 version 2004 (build 19041) or newer, and Windows 11 x64. The binaries are self-contained; a separate .NET runtime installation is not required.
