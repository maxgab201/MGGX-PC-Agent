# MGGX PC Agent

Native Windows companion for MGGX PC Control. It provides an authenticated, explicit-action API to the Android relay, starts automatically with Windows, monitors Sunshine and Tailscale, and includes a compact WinUI 3 control panel.

## Components

- `MGGX.PCAgent.Service.exe`: low-overhead Windows Service and HTTP/UDP host.
- `MGGX.PCAgent.exe`: native WinUI 3 status/configuration panel. Closing it does not stop the service.
- Inno Setup installer: registers delayed-auto startup, recovery actions, and restricted Windows Firewall rules.

Defaults: service `MGGXPCAgent`, API `http://0.0.0.0:8766`, discovery UDP `8767`, API v1. Data and 7-day rolling logs live in `%ProgramData%\MGGX\PC-Agent`.

Security: 256-bit random token protected with Windows DPAPI (`LocalMachine`) and an ACL limited to SYSTEM/Administrators; constant-time token comparison; authenticated power/service actions; fixed-window rate limiting; no shell, command execution, uploads, monitor control, camera, WebView, Chromium, or cloud dependency.

## Build

The supported production build runs on `windows-latest`:

```powershell
choco install innosetup -y
./scripts/build.ps1
```

Output: `artifacts/MGGX-PC-Agent-Setup-x64.exe`, portable ZIP, and `SHA256SUMS.txt`. Tests use fake power operations and never shut down the runner. See [Relay API](docs/RELAY_API.md) and [Android contract](docs/ANDROID_CONTRACT.md).

## Compatibility

Windows 10 version 2004 (build 19041) or newer, and Windows 11 x64. The binaries are self-contained; a separate .NET runtime installation is not required.
